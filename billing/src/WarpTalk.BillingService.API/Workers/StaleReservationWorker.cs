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


        // 75 seconds allows for the 15s heartbeat + 60s Grace Period to completely expire.
        var cutoffTime = DateTime.UtcNow.AddSeconds(-75);

        var staleReservations = await unitOfWork.CreditTransactionRepository.FindAsync(
            tx => tx.Type == "reserve" && tx.Status == "pending" && tx.CreatedAt < cutoffTime,
            cancellationToken);

        var count = 0;
        foreach (var reserveTx in staleReservations)
        {
            if (string.IsNullOrEmpty(reserveTx.CorrelationId)) continue;

            // Since the Redis reservation might have already expired, we process the refund directly in the database/ledger.

            var existingRefund = await unitOfWork.CreditTransactionRepository.FirstOrDefaultAsync(
                tx => tx.CorrelationId == reserveTx.CorrelationId && tx.Type == "refund", cancellationToken);

            if (existingRefund != null) continue;

            reserveTx.Status = "rolled_back";
            unitOfWork.CreditTransactionRepository.Update(reserveTx);

            var sub = await unitOfWork.SubscriptionRepository.GetByIdAsync(reserveTx.SubscriptionId, cancellationToken);
            if (sub != null)
            {
                sub.CreditsRemaining += reserveTx.Amount;
                unitOfWork.SubscriptionRepository.Update(sub);
            }

            var refundTx = new CreditTransaction
            {
                SubscriptionId = reserveTx.SubscriptionId,
                UserId = reserveTx.UserId,
                Amount = reserveTx.Amount,
                Type = "refund",
                Description = "Auto-refund for stale reservation",
                ReferenceType = "CreditReservation",
                CorrelationId = reserveTx.CorrelationId,
                Status = "committed",
                BalanceAfter = sub?.CreditsRemaining ?? 0,
                CreatedAt = DateTime.UtcNow
            };

            await unitOfWork.CreditTransactionRepository.AddAsync(refundTx, cancellationToken);
            count++;
        }

        if (count > 0)
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Auto-refunded {Count} stale reservations.", count);
        }
    }
}
