using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Application.Mappers;
using WarpTalk.BillingService.Infrastructure.Options;

namespace WarpTalk.BillingService.Infrastructure.Workers;

public class StaleReservationWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<StaleReservationWorker> _logger;
    private readonly BillingWorkerOptions _options;

    public StaleReservationWorker(
        IServiceProvider serviceProvider,
        ILogger<StaleReservationWorker> logger,
        IOptions<BillingWorkerOptions> options)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("StaleReservationWorker started.");

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

            await Task.Delay(_options.StaleReservationInterval, stoppingToken);
        }

        _logger.LogInformation("StaleReservationWorker is stopping.");
    }

    private async Task ProcessStaleReservationsAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var redisStore = scope.ServiceProvider.GetRequiredService<IRedisBillingStore>();

        var expiredResult = await redisStore.GetExpiredReservationsAsync(DateTimeOffset.UtcNow, cancellationToken);
        if (!expiredResult.IsSuccess)
        {
            _logger.LogWarning("Failed to get expired reservations from Redis: {Error}", expiredResult.Error);
            return;
        }

        var count = 0;
        foreach (var reserve in expiredResult.Value ?? Array.Empty<RedisCreditReservationDto>())
        {
            var sub = await unitOfWork.SubscriptionRepository.GetByIdAsync(reserve.SubscriptionId, cancellationToken);
            if (sub != null)
            {
                sub.CreditsRemaining += reserve.Amount;
                unitOfWork.SubscriptionRepository.Update(sub);
            }

            var refundTx = CreditMapper.CreateStaleReservationRefundTransaction(
                reserve.SubscriptionId,
                sub?.UserId ?? Guid.Empty,
                reserve.Amount,
                sub?.CreditsRemaining ?? 0
            );

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
