using WarpTalk.Shared.Coordination;
using WarpTalk.NotificationService.Application.Services.EmailCms;

namespace WarpTalk.NotificationService.API.HostedServices;

/// <summary>
/// Sends audience emails (custom templates) at a steady rate: every tick it takes the next due
/// send and hands at most SendsPerMinute/12 emails to the provider, so a large audience is spread
/// out instead of bursting past the provider's limits.
///
/// Replicas share the work through the shared Redis lease (<see cref="IDistributedLockProvider"/>)
/// held for the tick — two replicas processing the same send would email people twice, because a
/// send picks its next pending recipients and only marks them sent afterwards. The lease is
/// RENEWED while the tick runs: the lock this replaced was a fixed 30-second LockTake, so a batch
/// whose provider calls took longer than that let the other replica's 5-second timer start the
/// same batch over. If the lease is lost mid-tick the tick is cancelled rather than overlapping.
/// When Redis is unreachable the tick is skipped: better late than twice. Every failure is caught
/// and logged; a send worker must never take the service down (see the Redis consumer workers
/// that did).
/// </summary>
public sealed class EmailCampaignWorker : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(30);
    internal const string LockResource = "notification:email-campaigns";

    private readonly IServiceScopeFactory _scopes;
    private readonly IDistributedLockProvider _locks;
    private readonly EmailCampaignOptions _options;
    private readonly ILogger<EmailCampaignWorker> _logger;

    public EmailCampaignWorker(
        IServiceScopeFactory scopes,
        IDistributedLockProvider locks,
        EmailCampaignOptions options,
        ILogger<EmailCampaignWorker> logger)
    {
        _scopes = scopes;
        _locks = locks;
        _options = options;
        _logger = logger;
    }

    private int BatchSize => Math.Max(1, (int)Math.Ceiling(_options.SendsPerMinute / (60.0 / Tick.TotalSeconds)));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Tick);
        do
        {
            try
            {
                await RunTickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "The email send worker failed a tick; it will try again.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>One tick, on whichever replica holds the lease. Internal for the tests.</summary>
    internal Task<ExclusiveTickOutcome> RunTickAsync(CancellationToken stoppingToken) =>
        _locks.TryRunExclusiveAsync(
            LockResource,
            LeaseDuration,
            async ct =>
            {
                await using var scope = _scopes.CreateAsyncScope();
                var sends = scope.ServiceProvider.GetRequiredService<IEmailCampaignService>();
                // Resolving a due send is one step and sending a batch is another; both count as the tick.
                await sends.ProcessNextAsync(BatchSize, ct);
            },
            _logger,
            stoppingToken);
}
