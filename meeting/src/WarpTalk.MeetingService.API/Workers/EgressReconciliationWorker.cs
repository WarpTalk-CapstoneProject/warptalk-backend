using WarpTalk.MeetingService.Application.Interfaces;

namespace WarpTalk.MeetingService.API.Workers;

/// <summary>
/// Runs the egress reconciliation sweep — see <see cref="IEgressReconciliation"/> for why it
/// exists at all (WT-371 #8: recording's only completion path was a webhook that was never
/// configured, and nothing could tell).
/// </summary>
public sealed class EgressReconciliationWorker : BackgroundService
{
    /// <summary>
    /// Two minutes, not ten seconds like <see cref="BreakoutExpiryWorker"/>. This is a FALLBACK:
    /// when the webhook works it finds nothing, and when it does not, a recording appearing two
    /// minutes after the meeting rather than instantly is a non-event. Each tick costs one
    /// LiveKit call per in-progress recording, which is almost always zero.
    /// </summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(2);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<EgressReconciliationWorker> _logger;

    public EgressReconciliationWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<EgressReconciliationWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<IEgressReconciliation>();
                var result = await service.ReconcileAsync(DateTime.UtcNow, stoppingToken);

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
}
