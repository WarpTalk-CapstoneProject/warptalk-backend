using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.Shared.Events;

namespace WarpTalk.BillingService.Application.Entitlements;

public interface IEntitlementChangePublisher
{
    /// <summary>
    /// Re-resolves the workspace and ENQUEUES a full entitlement snapshot on the billing outbox.
    ///
    /// Enqueue, not publish: the row is written through the caller's unit of work, so it commits in
    /// the same transaction as the subscription or plan change that caused it. A change that rolls
    /// back cannot leave an event announcing it, and an event that is written cannot be lost because
    /// Redis happened to be down at that instant — the dispatcher retries it later.
    ///
    /// The caller still owns SaveChangesAsync. This method deliberately does not commit.
    /// </summary>
    Task EnqueueAsync(Guid workspaceId, string reason, CancellationToken ct = default);
}

/// <summary>
/// WT-263: the propagation half of "push, don't pull".
///
/// Reuses the billing outbox that migration 029 already created and that nothing had ever written
/// to: <c>subscription.outbox_messages</c>, <see cref="IUnitOfWork.OutboxMessages"/>, the
/// <see cref="IOutboxClaimStore"/> SKIP LOCKED claim primitive, and the retention sweep in
/// BillingOutboxWorker. The envelope is the shared <see cref="EventEnvelope{T}"/> with an event type
/// registered in <see cref="BillingEventTypes"/>, exactly like the payment events — no parallel
/// mechanism, no second table.
/// </summary>
public sealed class EntitlementChangePublisher : IEntitlementChangePublisher
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IEntitlementResolver _resolver;
    private readonly ILogger<EntitlementChangePublisher> _logger;

    public EntitlementChangePublisher(
        IUnitOfWork unitOfWork,
        IEntitlementResolver resolver,
        ILogger<EntitlementChangePublisher> logger)
    {
        _unitOfWork = unitOfWork;
        _resolver = resolver;
        _logger = logger;
    }

    public async Task EnqueueAsync(Guid workspaceId, string reason, CancellationToken ct = default)
    {
        if (workspaceId == Guid.Empty)
        {
            return;
        }

        var map = await _resolver.ResolveAsync(workspaceId, ct);

        var payload = new EntitlementsChangedEventPayload(
            map.WorkspaceId,
            map.PlanSlug,
            map.HasActiveSubscription,
            map.ResolvedAt,
            reason,
            map.Entitlements
                .Select(entitlement => new ResolvedEntitlementPayload(
                    entitlement.Key,
                    entitlement.Value,
                    entitlement.Source))
                .ToList());

        var envelope = DomainEventEnvelope.Create(
            BillingEventTypes.EntitlementsChanged,
            EntitlementConstants.Producer,
            workspaceId.ToString(),
            payload,
            occurredAt: map.ResolvedAt);

        await _unitOfWork.OutboxMessages.AddAsync(
            new OutboxMessage
            {
                Id = envelope.EventId,
                EventType = envelope.EventType,
                SchemaVersion = envelope.SchemaVersion,
                OccurredAt = envelope.OccurredAt,
                Producer = envelope.Producer,
                CorrelationId = envelope.CorrelationId,
                CausationId = envelope.CausationId,
                WorkspaceId = workspaceId,
                PayloadJson = JsonSerializer.Serialize(envelope),
                AvailableAt = envelope.OccurredAt,
                CreatedAt = DateTime.UtcNow
            },
            ct);

        _logger.LogInformation(
            "Enqueued {EventType} for workspace {WorkspaceId} ({Reason}); plan {PlanSlug}.",
            BillingEventTypes.EntitlementsChanged,
            workspaceId,
            reason,
            map.PlanSlug ?? "none");
    }
}
