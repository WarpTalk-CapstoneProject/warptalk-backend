using WarpTalk.BillingService.Domain.Constants;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Infrastructure.Persistence;


namespace WarpTalk.BillingService.Infrastructure.Workers;

public class SubscriptionExpirationWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<SubscriptionExpirationWorker> _logger;
    private readonly TimeSpan _checkInterval = TimeSpan.FromHours(1);

    public SubscriptionExpirationWorker(IServiceProvider serviceProvider, ILogger<SubscriptionExpirationWorker> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SubscriptionExpirationWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ExpireSubscriptionsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred executing SubscriptionExpirationWorker.");
            }

            await Task.Delay(_checkInterval, stoppingToken);
        }

        _logger.LogInformation("SubscriptionExpirationWorker is stopping.");
    }

    private async Task ExpireSubscriptionsAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BillingDbContext>();

        var now = DateTime.UtcNow;

        var expiredSubscriptions = await context.Subscriptions
            .Where(s => s.IsActive && s.DeletedAt == null && s.CurrentPeriodEnd < now)
            .ToListAsync(cancellationToken);

        if (expiredSubscriptions.Count > 0)
        {
            foreach (var sub in expiredSubscriptions)
            {
                sub.IsActive = false;
                sub.Status = SubscriptionConstants.SubscriptionStatuses.Expired;
                sub.UpdatedAt = now;
            }

            await context.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Expired {Count} subscriptions.", expiredSubscriptions.Count);
        }
    }
}
