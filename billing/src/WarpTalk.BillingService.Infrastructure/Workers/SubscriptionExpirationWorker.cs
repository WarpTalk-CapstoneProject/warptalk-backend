using WarpTalk.Shared.Coordination;
using WarpTalk.BillingService.Domain.Constants;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WarpTalk.BillingService.Application.Entitlements;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Services;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Infrastructure.Options;
using WarpTalk.Shared.PlatformSettings;


namespace WarpTalk.BillingService.Infrastructure.Workers;

public class SubscriptionExpirationWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IDistributedLockProvider _locks;
    private readonly ILogger<SubscriptionExpirationWorker> _logger;
    private readonly BillingWorkerOptions _options;

    public SubscriptionExpirationWorker(
        IServiceProvider serviceProvider,
        ILogger<SubscriptionExpirationWorker> logger,
        IOptions<BillingWorkerOptions> options,
        IDistributedLockProvider locks)
    {
        _locks = locks;
        _serviceProvider = serviceProvider;
        _logger = logger;
        _options = options.Value;
    }

    /// <summary>Lease name this worker's ticks run under (one replica at a time).</summary>
    public const string LockResource = "billing:subscription-expiration";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SubscriptionExpirationWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // SINGLE RUNNER. xmin already prevents a double write; this stops the losing replica from
                // rolling back its batch with a concurrency error on every tick.
                await _locks.TryRunExclusiveAsync(
                    LockResource,
                    TimeSpan.FromMinutes(5),
                    ct => SweepAsync(ct),
                    _logger,
                    stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred executing SubscriptionExpirationWorker.");
            }

            await Task.Delay(_options.SubscriptionExpirationInterval, stoppingToken);
        }

        _logger.LogInformation("SubscriptionExpirationWorker is stopping.");
    }

    public async Task SweepAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var aiServiceStateStore = scope.ServiceProvider.GetService<IAiServiceStateStore>();

        var now = DateTime.UtcNow;

        var expiredSubscriptions = await unitOfWork.SubscriptionRepository.GetExpiredActiveSubscriptionsAsync(now, cancellationToken);
        var expiredWorkspaces = new List<Guid>();
        var suspendedTrialWorkspaces = new List<Guid>();

        if (expiredSubscriptions.Count > 0)
        {
            var expiredCount = 0;
            var suspendedTrials = 0;

            foreach (var sub in expiredSubscriptions)
            {
                if (sub.TrialEndsAt is not null && sub.TrialEndsAt <= now)
                {
                    sub.ServiceState = SubscriptionConstants.ServiceStates.Suspended;
                    sub.SuspendedReason = SubscriptionConstants.SuspendedReasons.TrialEnded;
                    sub.Status = SubscriptionConstants.SubscriptionStatuses.Active;
                    sub.IsActive = true;
                    suspendedTrials++;
                    suspendedTrialWorkspaces.Add(sub.WorkspaceId);

                    if (aiServiceStateStore is not null)
                    {
                        await aiServiceStateStore.SetAiServiceStateAsync(
                            sub.WorkspaceId,
                            sub.ServiceState,
                            sub.SuspendedReason,
                            cancellationToken);
                    }
                }
                else
                {
                    sub.IsActive = false;
                    sub.Status = SubscriptionConstants.SubscriptionStatuses.Expired;
                    expiredCount++;
                    expiredWorkspaces.Add(sub.WorkspaceId);
                }

                sub.UpdatedAt = now;
                unitOfWork.SubscriptionRepository.Update(sub);
            }

            await unitOfWork.SaveChangesAsync(cancellationToken);
            _logger.LogInformation(
                "Processed expired subscriptions. Expired={ExpiredCount}, SuspendedTrials={SuspendedTrials}.",
                expiredCount,
                suspendedTrials);

            // THE LEAK THIS CLOSES. Expiring a subscription used to be a row update and nothing
            // else, so every reader that does not query billing kept the answer it had a moment
            // before: the workspace's entitlement snapshot still said has_active_subscription (so
            // WT-515 let it create rooms), the Redis AI-service flag Start Translation reads was
            // never set, and billing_worker, finding no active row, returned without charging OR
            // stopping anything. A workspace expired on 23 Sep translated a meeting free on 24 Sep.
            if (aiServiceStateStore is not null)
            {
                foreach (var workspaceId in expiredWorkspaces.Distinct())
                {
                    var pushed = await aiServiceStateStore.SetAiServiceStateAsync(
                        workspaceId,
                        SubscriptionConstants.ServiceStates.Suspended,
                        SubscriptionConstants.SuspendedReasons.SubscriptionExpired,
                        cancellationToken);
                    if (!pushed.IsSuccess)
                    {
                        _logger.LogWarning(
                            "subscription_expired_ai_state_push_failed WorkspaceId={WorkspaceId} Error={Error}. "
                            + "The entitlement snapshot and settlement still refuse the workspace.",
                            workspaceId,
                            pushed.Error);
                    }
                }
            }

            await RefreshEntitlementsAsync(
                scope.ServiceProvider,
                unitOfWork,
                expiredWorkspaces.Concat(suspendedTrialWorkspaces).Distinct().ToList(),
                cancellationToken);
        }

        await SettleEndedCreditsAsync(scope.ServiceProvider, now, cancellationToken);
    }

    /// <summary>
    /// Republishes the entitlement snapshot of every workspace whose subscription just ended, so the
    /// paywall (WT-515) closes now rather than whenever something else happens to republish.
    /// After the business save on purpose: the resolver reads committed rows.
    /// </summary>
    private async Task RefreshEntitlementsAsync(
        IServiceProvider services,
        IUnitOfWork unitOfWork,
        IReadOnlyList<Guid> workspaceIds,
        CancellationToken cancellationToken)
    {
        var publisher = services.GetService<IEntitlementChangePublisher>();
        if (publisher is null || workspaceIds.Count == 0)
        {
            return;
        }

        try
        {
            foreach (var workspaceId in workspaceIds)
            {
                await publisher.EnqueueAsync(workspaceId, EntitlementConstants.Reasons.SubscriptionExpired, cancellationToken);
            }

            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The expiry is committed. The hourly reconcile republishes lapsed workspaces too.
            _logger.LogError(ex, "subscription_expired_entitlements_publish_failed Count={Count}", workspaceIds.Count);
        }
    }

    /// <summary>
    /// The instant the forfeit half of the frozen-credit policy took effect. Subscriptions that
    /// ended before it are grandfathered (frozen whole). The platform setting wins when an operator
    /// set one; otherwise it is the moment migration 20260926090000 ran, which recorded itself in
    /// billing_policy_config. Neither available means null, and null grandfathers everything — the
    /// only reading that cannot forfeit credit by mistake.
    /// </summary>
    public static async Task<DateTime?> ResolvePolicyEffectiveAtAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var settings = services.GetService<IPlatformSettings>();
        if (settings is not null)
        {
            var configured = await settings.GetStringAsync(FrozenCreditDefaults.PolicyEffectiveAtKey, string.Empty, ct: cancellationToken);
            if (!string.IsNullOrWhiteSpace(configured)
                && DateTime.TryParse(
                    configured,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                    out var parsed))
            {
                return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
            }
        }

        var policy = services.GetService<IBillingPolicyRepository>();
        if (policy is null)
        {
            return null;
        }

        var epoch = await policy.ReadPolicyValueAsync(FrozenCreditDefaults.PolicyEffectiveEpochKey, -1m, cancellationToken);
        return epoch > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(epoch * 1000m, MidpointRounding.AwayFromZero)).UtcDateTime
            : null;
    }

    /// <summary>
    /// Freezes the credits of subscriptions that ended, releases frozen credits into renewed ones,
    /// and marks old frozen credits dormant. See CreditFreezeService for the policy.
    /// </summary>
    private async Task SettleEndedCreditsAsync(IServiceProvider services, DateTime now, CancellationToken cancellationToken)
    {
        var freezer = services.GetService<ICreditFreezeService>();
        if (freezer is null || !_options.FrozenCreditSweepEnabled)
        {
            return;
        }

        try
        {
            var policyEffectiveAt = await ResolvePolicyEffectiveAtAsync(services, cancellationToken);
            var split = await freezer.SplitEndedSubscriptionsAsync(now, policyEffectiveAt, cancellationToken);
            var released = await freezer.ReleaseFrozenCreditsAsync(now, cancellationToken);

            var settings = services.GetService<IPlatformSettings>();
            var graceDays = settings is null
                ? FrozenCreditDefaults.GraceDays
                : await settings.GetInt32Async(FrozenCreditDefaults.GraceDaysKey, FrozenCreditDefaults.GraceDays, ct: cancellationToken);
            var dormant = await freezer.MarkDormantAsync(now, graceDays, cancellationToken);

            if (split + released + dormant > 0)
            {
                _logger.LogInformation(
                    "ended_subscription_credits_settled Split={Split} Released={Released} Dormant={Dormant} GraceDays={GraceDays}",
                    split, released, dormant, graceDays);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "ended_subscription_credits_failed; the next sweep retries.");
        }
    }
}
