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

        // 1. Clean up expired sessions
        var expiredSessions = await redisStore.GetExpiredSessionsAsync(now, cancellationToken);
        foreach (var sessionId in expiredSessions)
        {
            await redisStore.RemoveSessionAsync(sessionId, cancellationToken);
            _logger.LogInformation("Session {SessionId} expired and removed from Redis.", sessionId);
        }

        // 2. Refund expired reservations
        var expiredReservations = await redisStore.GetAndRemoveExpiredReservationsAsync(now, cancellationToken);
        var hasRefunds = false;

        foreach (var reservation in expiredReservations)
        {
            // Refund logic
            var sub = await unitOfWork.SubscriptionRepository.GetByIdAsync(reservation.SubscriptionId, cancellationToken);
            if (sub != null)
            {
                sub.CreditsRemaining += reservation.Amount;
                sub.UpdatedAt = now.UtcDateTime;
                unitOfWork.SubscriptionRepository.Update(sub);
                _logger.LogInformation("Refunded {Amount} credits for expired reservation {ReservationId}.", reservation.Amount, reservation.IdempotencyKey);
                hasRefunds = true;
            }
        }

        if (hasRefunds)
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
    }
}
