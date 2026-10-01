using WarpTalk.Shared.Coordination;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WarpTalk.BillingService.Application.Entitlements;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Infrastructure.Options;

namespace WarpTalk.BillingService.Infrastructure.Workers;

/// <summary>
/// WT-430: re-resolves every workspace's entitlements on a slow interval and republishes them.
///
/// WHY THIS EXISTS
///   Enforcement does not ask billing anything. Each consumer keeps a local snapshot, and that
///   snapshot is only rewritten when billing publishes billing.entitlements_changed — which only
///   three methods do, all of them reacting to a mutation made THROUGH billing. Any change that
///   reaches the billing database another way leaves every consumer enforcing an answer that is
///   quietly, permanently wrong.
///
///   That is not hypothetical. A production subscription's status was corrected directly; two days
///   later the workspace was still being enforced against the snapshot resolved BEFORE the change —
///   platform defaults of 5 rooms and 2 participants, with voice cloning and the assistant off,
///   under an Enterprise plan. Nothing was broken, nothing alerted, and nothing would ever have
///   fixed it: the publish path was healthy and simply had no reason to fire.
///
/// WHY REPUBLISH UNCONDITIONALLY RATHER THAN DIFF
///   Billing does not hold the consumers' snapshots, so it cannot know whether one has drifted. It
///   would have to store its own copy of what it last sent — a second source of truth, which is the
///   class of thing this sweep exists to repair. The consumer is idempotent: it overwrites the
///   snapshot with whatever arrives, so a redundant event costs one row and one write.
///
/// COST
///   One outbox row per workspace per interval (default hourly), swept by the same retention job
///   that already trims the outbox. Set the interval to 0 to switch it off.
/// </summary>
public class EntitlementReconcileWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IDistributedLockProvider _locks;
    private readonly ILogger<EntitlementReconcileWorker> _logger;
    private readonly BillingWorkerOptions _options;

    public EntitlementReconcileWorker(
        IServiceProvider serviceProvider,
        ILogger<EntitlementReconcileWorker> logger,
        IOptions<BillingWorkerOptions> options,
        IDistributedLockProvider locks)
    {
        _locks = locks;
        _serviceProvider = serviceProvider;
        _logger = logger;
        _options = options.Value;
    }

    /// <summary>Lease name this worker's ticks run under (one replica at a time).</summary>
    public const string LockResource = "billing:entitlement-reconcile";

    /// <summary>How long after a subscription ended its workspace is still republished.</summary>
    public const int LapsedLookbackDays = 45;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.EntitlementReconcileIntervalMinutes <= 0)
        {
            _logger.LogInformation(
                "EntitlementReconcileWorker is disabled (EntitlementReconcileIntervalMinutes = {Interval}). "
                + "Consumer entitlement snapshots will only be refreshed by billing mutations.",
                _options.EntitlementReconcileIntervalMinutes);
            return;
        }

        _logger.LogInformation(
            "EntitlementReconcileWorker started; sweeping every {Interval}.",
            _options.EntitlementReconcileInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            // Sweep first, then wait. A service that has just started is the most likely moment for
            // a snapshot to be stale — a direct data change is usually followed by a restart.
            try
            {
                // ONCE PER INTERVAL CLUSTER-WIDE. Each sweep writes a fresh outbox row (new event id) per
                // workspace, so N replicas published N entitlement events per workspace per interval. The
                // lease is kept for 90% of the interval, so the other replicas' timers skip until then.
                await _locks.TryRunExclusiveAsync(
                    LockResource,
                    TimeSpan.FromTicks((long)(_options.EntitlementReconcileInterval.Ticks * 0.9)),
                    ct => ReconcileAsync(ct),
                    _logger,
                    stoppingToken,
                    holdAfterCompletion: true);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred executing EntitlementReconcileWorker.");
            }

            try
            {
                await Task.Delay(_options.EntitlementReconcileInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("EntitlementReconcileWorker is stopping.");
    }

    /// <summary>Public so a test can drive one sweep without running the loop.</summary>
    public async Task ReconcileAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var publisher = scope.ServiceProvider.GetRequiredService<IEntitlementChangePublisher>();

        var subscriptions = await unitOfWork.SubscriptionRepository.GetActiveSubscriptionsAsync(ct);

        // LAPSED WORKSPACES TOO. This swept active rows only, and expiring a subscription is exactly
        // what removes a row from that set — so the one change the paywall most needed to hear about
        // was the one this sweep could never repair. A workspace that expired on 23 Sep kept a
        // snapshot saying has_active_subscription = true, and WT-515 let it keep creating rooms.
        // Rows that ended within the lookback are republished (to has_active_subscription = false)
        // until they age out; older ones were settled by earlier sweeps.
        var lapsedSince = DateTime.UtcNow.AddDays(-LapsedLookbackDays);
        var lapsed = await unitOfWork.SubscriptionRepository.FindAsync(
            subscription => !subscription.IsActive
                && subscription.DeletedAt == null
                && subscription.UpdatedAt >= lapsedSince,
            ct) ?? Array.Empty<Domain.Entities.Subscription>();

        // Distinct: a workspace with more than one subscription row must not be enqueued twice, and
        // the resolver answers per workspace regardless of which row prompted it.
        var workspaceIds = subscriptions
            .Concat(lapsed)
            .Select(subscription => subscription.WorkspaceId)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();

        if (workspaceIds.Count == 0)
        {
            return;
        }

        foreach (var workspaceId in workspaceIds)
        {
            ct.ThrowIfCancellationRequested();
            await publisher.EnqueueAsync(workspaceId, EntitlementConstants.Reasons.Backfill, ct);
        }

        // One commit for the sweep — EnqueueAsync writes through the unit of work and deliberately
        // does not commit, so nothing reaches the outbox until this line.
        await unitOfWork.SaveChangesAsync(ct);

        _logger.LogInformation(
            "EntitlementReconcileWorker enqueued {Count} entitlement snapshots.",
            workspaceIds.Count);
    }
}
