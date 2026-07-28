using WarpTalk.MeetingService.Application.Interfaces;

namespace WarpTalk.MeetingService.API.Workers;

public sealed class BreakoutExpiryWorker : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<BreakoutExpiryWorker> _logger;

    public BreakoutExpiryWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<BreakoutExpiryWorker> logger)
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
                var service = scope.ServiceProvider.GetRequiredService<IBreakoutsService>();
                var result = await service.ExpireDueBreakoutsAsync(
                    DateTime.UtcNow,
                    stoppingToken);
                if (!result.IsSuccess)
                {
                    _logger.LogWarning(
                        "Breakout expiry scan failed: {ErrorCode} {Error}",
                        result.ErrorCode,
                        result.Error);
                }
                else if (result.Value > 0)
                {
                    _logger.LogInformation(
                        "Expired {BreakoutSessionCount} breakout sessions",
                        result.Value);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Breakout expiry scan failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
