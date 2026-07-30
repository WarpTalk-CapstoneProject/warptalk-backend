using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Domain.Interfaces;

namespace WarpTalk.BillingService.API.Workers;

public class SessionMonitorWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<SessionMonitorWorker> _logger;

    public SessionMonitorWorker(IServiceProvider serviceProvider, ILogger<SessionMonitorWorker> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Session Monitor Worker started.");

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

            // Run every 5 seconds
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    private async Task CheckSessionsAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var redisStore = scope.ServiceProvider.GetRequiredService<IRedisBillingStore>();

        var now = DateTimeOffset.UtcNow;

        // Grace period is 75s. Any session that expires in Redis means it missed heartbeats and exceeded the 60s grace period.
        // We only remove the session from Redis. The StaleReservationWorker will handle refunding its pending reservations safely via the Ledger.
        var expiredSessions = await redisStore.GetExpiredSessionsAsync(now, cancellationToken);
        foreach (var sessionId in expiredSessions)
        {
            await redisStore.RemoveSessionAsync(sessionId, cancellationToken);
            _logger.LogInformation("Session {SessionId} missed heartbeats and exceeded 60s grace period. Terminated.", sessionId);
        }
    }
}
