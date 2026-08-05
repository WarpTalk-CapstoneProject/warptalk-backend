using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.Shared.Events;

namespace WarpTalk.BillingService.Infrastructure.Workers;

/// <summary>
/// Billing's transactional-outbox pump.
///
/// WT-263 completed it. The retention sweep below has been here since migration 029, but nothing
/// ever DISPATCHED: the SKIP LOCKED claim primitive existed with no caller, so the table had no
/// writers and no readers. It now drains claimed rows onto Redis, which is what makes
/// billing.entitlements_changed reach the services that enforce it.
/// </summary>
public sealed class BillingOutboxWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<BillingOutboxWorker> logger) : BackgroundService
{
    private const int DispatchBatchSize = 64;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextPurgeAt = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTimeOffset.UtcNow;

                await DispatchAsync(stoppingToken);

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
                // Every failure stays inside the loop. This is a BackgroundService: an escaping
                // exception trips BackgroundServiceExceptionBehavior.StopHost and takes the whole
                // billing process down, turning a Redis or Postgres blip into an outage.
                logger.LogError(exception, "Billing outbox cycle failed.");
            }

            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }

    private async Task DispatchAsync(CancellationToken stoppingToken)
    {
        using var scope = scopeFactory.CreateScope();
        var claimStore = scope.ServiceProvider.GetRequiredService<IOutboxClaimStore>();
        var publisher = scope.ServiceProvider.GetRequiredService<IBillingMessagePublisher>();

        var claimed = await claimStore.ClaimAsync(DispatchBatchSize, DateTime.UtcNow, stoppingToken);
        foreach (var message in claimed)
        {
            try
            {
                var channel = ChannelFor(message.EventType);
                if (channel == null)
                {
                    // An unroutable event type must not spin forever at the head of the queue. It is
                    // stamped published and logged, because the alternative — retrying an event no
                    // channel accepts — starves every event behind it.
                    logger.LogWarning(
                        "Billing outbox event {EventId} has unroutable type '{EventType}'; marking published without dispatch.",
                        message.Id,
                        message.EventType);
                    await claimStore.MarkPublishedAsync(message.Id, DateTime.UtcNow, stoppingToken);
                    continue;
                }

                // PayloadJson already holds the serialized EventEnvelope, so it goes out verbatim —
                // re-wrapping it here would hand consumers a second envelope shape to parse.
                await publisher.PublishRawAsync(channel, message.PayloadJson, stoppingToken);
                await claimStore.MarkPublishedAsync(message.Id, DateTime.UtcNow, stoppingToken);
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "Failed to dispatch billing outbox event {EventId} ({EventType}); it will be retried.",
                    message.Id,
                    message.EventType);
                await claimStore.ReleaseFailedAsync(message.Id, exception.Message, stoppingToken);
            }
        }
    }

    private static string? ChannelFor(string eventType) => eventType switch
    {
        BillingEventTypes.EntitlementsChanged => BillingEventTypes.EntitlementsChangedChannel,
        _ => null
    };
}
