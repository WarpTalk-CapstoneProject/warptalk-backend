using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Domain.Interfaces;

namespace WarpTalk.BillingService.Infrastructure.Workers;

public sealed class BillingOutboxWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<BillingOutboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextPurgeAt = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTimeOffset.UtcNow;
                if (now >= nextPurgeAt)
                {
                    using var scope = scopeFactory.CreateScope();
                    var outboxClaimStore = scope.ServiceProvider.GetRequiredService<IOutboxClaimStore>();
                    var purged = await outboxClaimStore.PurgePublishedBeforeAsync(
                        now.AddDays(-7).UtcDateTime,
                        stoppingToken);

                    if (purged > 0)
                    {
                        logger.LogInformation(
                            "Purged {Count} published billing outbox event(s).",
                            purged);
                    }

                    nextPurgeAt = now.AddHours(1);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Billing outbox retention cycle failed.");
            }

            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }
}
