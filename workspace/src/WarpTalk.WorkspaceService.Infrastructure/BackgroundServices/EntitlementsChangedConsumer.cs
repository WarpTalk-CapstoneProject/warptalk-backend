using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using WarpTalk.Shared.Events;
using WarpTalk.WorkspaceService.Application.Entitlements;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Interfaces;

namespace WarpTalk.WorkspaceService.Infrastructure.BackgroundServices;

/// <summary>
/// WT-263: keeps this service's local entitlement snapshot up to date from
/// billing.entitlements_changed.
///
/// GUARDED, and that is not incidental. An exception escaping <see cref="ExecuteAsync"/> in a
/// BackgroundService trips the default BackgroundServiceExceptionBehavior.StopHost and takes the
/// ENTIRE WorkspaceService process down — not just this worker. WarpTalk has already shipped that
/// outage twice (HostFallbackConsumerWorker, ParticipantOfflineConsumerWorker), both times because
/// the app and infra roles deploy in parallel and this code reached SubscribeAsync before Redis was
/// accepting connections. The subscribe is therefore wrapped in the same bounded-backoff retry loop
/// those two now use, and every message handler body is wrapped in its own catch: one malformed
/// event must not kill the subscription, and a dead subscription must not kill the service.
///
/// Degradation is safe by construction. If this worker never starts, enforcement keeps serving the
/// last snapshot that reached the database — stale, but never absent, and never a denial.
/// </summary>
public class EntitlementsChangedConsumer : BackgroundService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<EntitlementsChangedConsumer> _logger;

    public EntitlementsChangedConsumer(
        IConnectionMultiplexer redis,
        IServiceProvider serviceProvider,
        ILogger<EntitlementsChangedConsumer> logger)
    {
        _redis = redis;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var subscriber = _redis.GetSubscriber();

        var retryDelay = TimeSpan.FromSeconds(2);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await subscriber.SubscribeAsync(
                    RedisChannel.Literal(BillingEventTypes.EntitlementsChangedChannel),
                    async (_, message) => await HandleAsync(message, stoppingToken));

                _logger.LogInformation(
                    "EntitlementsChangedConsumer started subscribing to '{Channel}'.",
                    BillingEventTypes.EntitlementsChangedChannel);
                break;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(
                    ex,
                    "EntitlementsChangedConsumer could not subscribe to '{Channel}'; retrying in {RetryDelay}.",
                    BillingEventTypes.EntitlementsChangedChannel,
                    retryDelay);
                await Task.Delay(retryDelay, stoppingToken);
                retryDelay = TimeSpan.FromSeconds(Math.Min(retryDelay.TotalSeconds * 2, 30));
            }
        }

        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private async Task HandleAsync(RedisValue message, CancellationToken ct)
    {
        try
        {
            var payload = message.ToString();
            if (string.IsNullOrEmpty(payload))
            {
                return;
            }

            if (!TryParseEnvelope(payload, out var envelope) || envelope == null)
            {
                return;
            }

            using var scope = _serviceProvider.CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            await ApplyAsync(unitOfWork, envelope, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "EntitlementsChangedConsumer failed to process an entitlements.changed message.");
        }
    }

    /// <summary>
    /// Rejects anything that is not the event and schema this consumer understands, in the same
    /// shape MeetingStartedEventConsumer.TryParseEvent already uses in this service.
    /// </summary>
    public static bool TryParseEnvelope(
        string serializedEvent,
        out EventEnvelope<EntitlementsChangedEventPayload>? envelope)
    {
        envelope = null;
        try
        {
            var parsed = JsonSerializer.Deserialize<EventEnvelope<EntitlementsChangedEventPayload>>(serializedEvent);
            if (parsed == null
                || parsed.EventType != BillingEventTypes.EntitlementsChanged
                || parsed.SchemaVersion != DomainEventEnvelope.CurrentSchemaVersion
                || parsed.Payload == null
                || parsed.Payload.WorkspaceId == Guid.Empty
                || parsed.Payload.Entitlements == null)
            {
                return false;
            }

            envelope = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Upserts the snapshot. Public so the propagation path is testable without Redis.
    ///
    /// IDEMPOTENT BY VALUE, not by an inbox table. The payload is a FULL snapshot rather than a
    /// delta, so re-applying the same event is a no-op and the at-least-once delivery the outbox
    /// dispatcher provides needs no dedupe row. What does matter is ORDER, so an event that resolved
    /// no later than the stored one is dropped — otherwise a redelivered old event could roll a
    /// workspace back onto a plan it has already left.
    /// </summary>
    public async Task ApplyAsync(
        IUnitOfWork unitOfWork,
        EventEnvelope<EntitlementsChangedEventPayload> envelope,
        CancellationToken ct)
    {
        var payload = envelope.Payload;

        var entitlementsJson = JsonSerializer.Serialize(
            payload.Entitlements.ToDictionary(
                entitlement => entitlement.Key,
                entitlement => new StoredEntitlement(entitlement.Value, entitlement.Source),
                StringComparer.Ordinal));

        var existing = await unitOfWork.WorkspaceEntitlementSnapshotRepository
            .GetForWorkspaceAsync(payload.WorkspaceId, ct);

        if (existing == null)
        {
            await unitOfWork.WorkspaceEntitlementSnapshotRepository.AddAsync(
                new WorkspaceEntitlementSnapshot
                {
                    WorkspaceId = payload.WorkspaceId,
                    EntitlementsJson = entitlementsJson,
                    PlanSlug = payload.PlanSlug,
                    HasActiveSubscription = payload.HasActiveSubscription,
                    ResolvedAt = payload.ResolvedAt,
                    LastEventId = envelope.EventId,
                    UpdatedAt = DateTime.UtcNow
                },
                ct);
        }
        else
        {
            if (existing.ResolvedAt > payload.ResolvedAt)
            {
                _logger.LogDebug(
                    "Ignoring stale entitlements.changed for workspace {WorkspaceId}: event resolved {EventResolvedAt}, snapshot {SnapshotResolvedAt}.",
                    payload.WorkspaceId,
                    payload.ResolvedAt,
                    existing.ResolvedAt);
                return;
            }

            existing.EntitlementsJson = entitlementsJson;
            existing.PlanSlug = payload.PlanSlug;
            existing.HasActiveSubscription = payload.HasActiveSubscription;
            existing.ResolvedAt = payload.ResolvedAt;
            existing.LastEventId = envelope.EventId;
            existing.UpdatedAt = DateTime.UtcNow;
            unitOfWork.WorkspaceEntitlementSnapshotRepository.Update(existing);
        }

        await unitOfWork.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Applied entitlement snapshot for workspace {WorkspaceId} (plan {PlanSlug}, reason {Reason}).",
            payload.WorkspaceId,
            payload.PlanSlug ?? "none",
            payload.Reason);
    }
}
