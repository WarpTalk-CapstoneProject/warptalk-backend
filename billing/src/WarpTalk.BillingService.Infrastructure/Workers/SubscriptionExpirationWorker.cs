using WarpTalk.BillingService.Domain.Constants;
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Infrastructure.Options;


namespace WarpTalk.BillingService.Infrastructure.Workers;

public class SubscriptionExpirationWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<SubscriptionExpirationWorker> _logger;
    private readonly BillingWorkerOptions _options;

    public SubscriptionExpirationWorker(
        IServiceProvider serviceProvider,
        ILogger<SubscriptionExpirationWorker> logger,
        IOptions<BillingWorkerOptions> options)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SubscriptionExpirationWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred executing SubscriptionExpirationWorker.");
            }

            await Task.Delay(_options.SubscriptionExpirationInterval, stoppingToken);
        }

        _logger.LogInformation("SubscriptionExpirationWorker is stopping.");
    }

    public async Task SweepAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var redisStore = scope.ServiceProvider.GetService<IRedisBillingStore>();

        var now = DateTime.UtcNow;

        var expiredSubscriptions = await unitOfWork.SubscriptionRepository.GetExpiredActiveSubscriptionsAsync(now, cancellationToken);

        if (expiredSubscriptions.Count > 0)
        {
            var expiredCount = 0;
            var suspendedTrials = 0;

            foreach (var sub in expiredSubscriptions)
            {
                if (sub.TrialEndsAt is not null && sub.TrialEndsAt <= now)
                {
                    sub.ServiceState = SubscriptionConstants.ServiceStates.Suspended;
                    sub.SuspendedReason = SubscriptionConstants.SuspendedReasons.TrialEnded;
                    sub.Status = SubscriptionConstants.SubscriptionStatuses.Active;
                    sub.IsActive = true;
                    suspendedTrials++;

                    if (redisStore is not null)
                    {
                        await redisStore.SetAiServiceStateAsync(
                            sub.WorkspaceId,
                            sub.ServiceState,
                            sub.SuspendedReason,
                            cancellationToken);
                    }
                }
                else
                {
                    sub.IsActive = false;
                    sub.Status = SubscriptionConstants.SubscriptionStatuses.Expired;
                    expiredCount++;
                }

                sub.UpdatedAt = now;
                unitOfWork.SubscriptionRepository.Update(sub);
            }

            await unitOfWork.SaveChangesAsync(cancellationToken);
            _logger.LogInformation(
                "Processed expired subscriptions. Expired={ExpiredCount}, SuspendedTrials={SuspendedTrials}.",
                expiredCount,
                suspendedTrials);
        }
    }
}
