using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Infrastructure.Helpers;
using WarpTalk.BillingService.Infrastructure.Options;

namespace WarpTalk.BillingService.Infrastructure.Workers;

/// <summary>
/// Periodically renews active auto-renew subscriptions that are close to the end of the current period.
/// </summary>
public class SubscriptionRenewalWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<SubscriptionRenewalWorker> _logger;
    private readonly BillingWorkerOptions _options;

    public SubscriptionRenewalWorker(
        IServiceProvider serviceProvider,
        ILogger<SubscriptionRenewalWorker> logger,
        IOptions<BillingWorkerOptions> options)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SubscriptionRenewalWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RenewSubscriptionsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SubscriptionRenewalWorker: error during renewal cycle.");
            }

            await Task.Delay(_options.SubscriptionRenewalInterval, stoppingToken);
        }

        _logger.LogInformation("SubscriptionRenewalWorker is stopping.");
    }

    private async Task RenewSubscriptionsAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var now = DateTime.UtcNow;
        var renewalThreshold = now.Add(_options.SubscriptionRenewalWindow);

        var dueForRenewal = await unitOfWork.SubscriptionRepository.GetDueForRenewalAsync(
            renewalThreshold,
            now.Subtract(_options.SubscriptionRenewalLookback),
            cancellationToken);

        if (dueForRenewal.Count == 0)
            return;

        _logger.LogInformation(
            "SubscriptionRenewalWorker: found {Count} subscription(s) due for renewal.",
            dueForRenewal.Count);

        var renewed = 0;
        foreach (var subscription in dueForRenewal)
        {
            try
            {
                await SubscriptionRenewalHelper.RenewOneAsync(unitOfWork, subscription, cancellationToken);
                renewed++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SubscriptionRenewalWorker: failed to renew subscription {SubId}.", subscription.Id);
            }
        }

        if (renewed > 0)
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("SubscriptionRenewalWorker: successfully renewed {Count} subscription(s).", renewed);
        }
    }
}
