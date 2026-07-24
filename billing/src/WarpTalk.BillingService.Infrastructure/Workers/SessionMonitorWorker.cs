using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Infrastructure.Options;

namespace WarpTalk.BillingService.Infrastructure.Workers;

public class SessionMonitorWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<SessionMonitorWorker> _logger;
    private readonly BillingWorkerOptions _options;

    public SessionMonitorWorker(
        IServiceProvider serviceProvider,
        ILogger<SessionMonitorWorker> logger,
        IOptions<BillingWorkerOptions> options)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SessionMonitorWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckSessionsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred during session monitoring.");
            }

            await Task.Delay(_options.SessionMonitorInterval, stoppingToken);
        }

        _logger.LogInformation("SessionMonitorWorker is stopping.");
    }

    private async Task CheckSessionsAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var redisStore = scope.ServiceProvider.GetRequiredService<IRedisBillingStore>();

        var now = DateTimeOffset.UtcNow;

        var expiredResult = await redisStore.GetExpiredSessionsAsync(now, cancellationToken);
        if (!expiredResult.IsSuccess)
        {
            _logger.LogWarning("Failed to get expired sessions from Redis: {Error}", expiredResult.Error);
            return;
        }

        foreach (var sessionId in expiredResult.Value ?? Array.Empty<Guid>())
        {
            await redisStore.RemoveSessionAsync(sessionId, cancellationToken);
            _logger.LogInformation("Session {SessionId} missed heartbeats and exceeded 60s grace period. Terminated.", sessionId);
        }
    }
}
