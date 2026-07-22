using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.API.Workers;

public class StaleReservationWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<StaleReservationWorker> _logger;

    public StaleReservationWorker(IServiceProvider serviceProvider, ILogger<StaleReservationWorker> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("StaleReservationWorker is starting.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessStaleReservationsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred while processing stale reservations.");
            }

            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }

    private async Task ProcessStaleReservationsAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var redisStore = scope.ServiceProvider.GetRequiredService<IRedisBillingStore>();

        var expiredReservations = await redisStore.GetExpiredReservationsAsync(DateTimeOffset.UtcNow, cancellationToken);

        var count = 0;
        foreach (var reserve in expiredReservations)
        {
            var sub = await unitOfWork.SubscriptionRepository.GetByIdAsync(reserve.SubscriptionId, cancellationToken);
            if (sub != null)
            {
                sub.CreditsRemaining += reserve.Amount;
                unitOfWork.SubscriptionRepository.Update(sub);
            }

            var refundTx = new CreditTransaction
            {
                SubscriptionId = reserve.SubscriptionId,
                UserId = sub?.UserId ?? Guid.Empty,
                Amount = reserve.Amount,
                Type = "refund",
                Description = "Auto-refund for stale reservation",
                ReferenceType = "CreditReservation",
                BalanceAfter = sub?.CreditsRemaining ?? 0,
                CreatedAt = DateTime.UtcNow
            };

            await unitOfWork.CreditTransactionRepository.AddAsync(refundTx, cancellationToken);
            await redisStore.RemoveReservationAsync(reserve.IdempotencyKey, cancellationToken);
            count++;
        }

        if (count > 0)
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Auto-refunded {Count} stale reservations.", count);
        }
    }
}
