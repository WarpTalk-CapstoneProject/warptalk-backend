using WarpTalk.MeetingService.Application.Interfaces;
using WarpTalk.Shared.Coordination;

namespace WarpTalk.MeetingService.API.Workers;

/// <summary>
/// Runs the egress reconciliation sweep — see <see cref="IEgressReconciliation"/> for why it
/// exists at all (WT-371 #8: recording's only completion path was a webhook that was never
/// configured, and nothing could tell).
/// </summary>
public sealed class EgressReconciliationWorker : BackgroundService
{
    /// <summary>
    /// Two minutes. This is a fallback rather than the primary completion path:
    /// when the webhook works it finds nothing, and when it does not, a recording appearing two
    /// minutes after the meeting rather than instantly is a non-event. Each tick costs one
    /// LiveKit call per in-progress recording, which is almost always zero.
    /// </summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(2);

    /// <summary>
    /// One replica per tick. Each sweep asks LiveKit about every in-progress recording and
    /// publishes a completion event for each finished one; the consumer ignores duplicates, but N
    /// replicas meant N LiveKit calls and N events per recording.
    /// </summary>
    public const string LockResource = "meeting:egress-reconciliation";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<EgressReconciliationWorker> _logger;
    private readonly IDistributedLockProvider _locks;

    public EgressReconciliationWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<EgressReconciliationWorker> logger,
        IDistributedLockProvider locks)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _locks = locks;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            try
            {
                await _locks.TryRunExclusiveAsync(
                    LockResource,
                    TimeSpan.FromMinutes(1),
                    SweepOnceAsync,
                    _logger,
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Caught, never rethrown: an exception escaping ExecuteAsync trips the default
                // BackgroundServiceExceptionBehavior.StopHost and takes the whole meeting service
                // down — the failure mode HostFallbackConsumerWorker documents. A fallback sweep
                // must never be able to kill the thing it is backing up.
                _logger.LogError(ex, "Egress reconciliation sweep failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task SweepOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IEgressReconciliation>();
        var result = await service.ReconcileAsync(DateTime.UtcNow, cancellationToken);

        if (!result.IsSuccess)
        {
            _logger.LogWarning(
                "Egress reconciliation failed: {ErrorCode} {Error}",
                result.ErrorCode,
                result.Error);
        }
        else if (result.Value > 0)
        {
            _logger.LogInformation("Reconciled {EgressCount} finished egresses", result.Value);
        }
    }
}
