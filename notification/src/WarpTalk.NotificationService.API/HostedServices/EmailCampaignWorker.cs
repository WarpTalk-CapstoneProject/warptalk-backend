using StackExchange.Redis;
using WarpTalk.NotificationService.Application.Services.EmailCms;

namespace WarpTalk.NotificationService.API.HostedServices;

/// <summary>
/// Sends audience emails (custom templates) at a steady rate: every tick it takes the next due
/// send and hands at most SendsPerMinute/12 emails to the provider, so a large audience is spread
/// out instead of bursting past the provider's limits.
///
/// Replicas share the work through a Redis lock held for the tick — two replicas processing the
/// same send would email people twice. When Redis is unreachable the tick is skipped: better late
/// than twice. Every failure is caught and logged; a send worker must never take the service down
/// (see the Redis consumer workers that did).
/// </summary>
public sealed class EmailCampaignWorker : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LockTtl = TimeSpan.FromSeconds(30);
    private const string LockKey = "notification:email-campaigns:worker-lock";

    private readonly IServiceScopeFactory _scopes;
    private readonly IConnectionMultiplexer _redis;
    private readonly EmailCampaignOptions _options;
    private readonly ILogger<EmailCampaignWorker> _logger;
    private readonly string _owner = $"{Environment.MachineName}-{Environment.ProcessId}-{Guid.NewGuid():N}";

    public EmailCampaignWorker(
        IServiceScopeFactory scopes,
        IConnectionMultiplexer redis,
        EmailCampaignOptions options,
        ILogger<EmailCampaignWorker> logger)
    {
        _scopes = scopes;
        _redis = redis;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = Math.Max(1, (int)Math.Ceiling(_options.SendsPerMinute / (60.0 / Tick.TotalSeconds)));
        using var timer = new PeriodicTimer(Tick);
        do
        {
            try
            {
                await RunOnceAsync(batch, stoppingToken);
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

    private async Task RunOnceAsync(int batch, CancellationToken ct)
    {
        IDatabase database;
        try
        {
            database = _redis.GetDatabase();
            if (!await database.LockTakeAsync(LockKey, _owner, LockTtl)) return;
        }
        catch (RedisException ex)
        {
            _logger.LogWarning(ex, "Email sends paused: the worker lock is unavailable.");
            return;
        }

        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            var sends = scope.ServiceProvider.GetRequiredService<IEmailCampaignService>();
            // Resolving a due send is one step and sending a batch is another; both count as the tick.
            await sends.ProcessNextAsync(batch, ct);
        }
        finally
        {
            try
            {
                await database.LockReleaseAsync(LockKey, _owner);
            }
            catch (RedisException)
            {
                // The lock expires on its own.
            }
        }
    }
}
