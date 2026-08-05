using System.Diagnostics;
using System.Text.Json.Serialization;

namespace WarpTalk.Shared.Events;

public sealed record EventEnvelope<T>(
    [property: JsonPropertyName("event_id")] Guid EventId,
    [property: JsonPropertyName("event_type")] string EventType,
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("occurred_at")] DateTime OccurredAt,
    [property: JsonPropertyName("producer")] string Producer,
    [property: JsonPropertyName("correlation_id")] string? CorrelationId,
    [property: JsonPropertyName("causation_id")] string? CausationId,
    [property: JsonPropertyName("workspace_id")] string? WorkspaceId,
    [property: JsonPropertyName("payload")] T Payload
);

public static class DomainEventEnvelope
{
    public const int CurrentSchemaVersion = 1;

    public static EventEnvelope<T> Create<T>(
        string eventType,
        string producer,
        string? workspaceId,
        T payload,
        string? correlationId = null,
        string? causationId = null,
        DateTime? occurredAt = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        ArgumentException.ThrowIfNullOrWhiteSpace(producer);
        ArgumentNullException.ThrowIfNull(payload);

        correlationId ??= Activity.Current?.TraceId.ToString();

        return new EventEnvelope<T>(
            Guid.NewGuid(),
            eventType,
            CurrentSchemaVersion,
            occurredAt?.ToUniversalTime() ?? DateTime.UtcNow,
            producer,
            correlationId,
            causationId,
            workspaceId,
            payload);
    }
}

public sealed record BillingPaymentEventPayload(
    [property: JsonPropertyName("provider_transaction_id")] string ProviderTransactionId,
    [property: JsonPropertyName("stripe_session_id")] string StripeSessionId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("amount")] decimal Amount,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("payment_type")] string PaymentType,
    [property: JsonPropertyName("user_id")] string UserId,
    [property: JsonPropertyName("workspace_id")] string WorkspaceId,
    [property: JsonPropertyName("plan_slug")] string PlanSlug,
    [property: JsonPropertyName("billing_cycle")] string BillingCycle,
    [property: JsonPropertyName("failure_reason")] string? FailureReason
);

public sealed record MeetingRecordingCompletedEventPayload(
    [property: JsonPropertyName("translation_room_id")] Guid TranslationRoomId,
    [property: JsonPropertyName("egress_id")] string EgressId,
    [property: JsonPropertyName("file_url")] string FileUrl,
    [property: JsonPropertyName("file_format")] string FileFormat,
    [property: JsonPropertyName("file_size_bytes")] long? FileSizeBytes,
    [property: JsonPropertyName("contains_raw_audio")] bool ContainsRawAudio,
    [property: JsonPropertyName("contains_raw_video")] bool ContainsRawVideo
);

public sealed record MeetingStartedEventPayload(
    [property: JsonPropertyName("translation_room_id")] Guid TranslationRoomId,
    [property: JsonPropertyName("workspace_id")] Guid WorkspaceId,
    [property: JsonPropertyName("title")] string? Title = null,
    [property: JsonPropertyName("description")] string? Description = null
);

public sealed record MeetingTrackPublishedEventPayload(
    [property: JsonPropertyName("room_name")] string RoomName,
    [property: JsonPropertyName("participant_identity")] string? ParticipantIdentity,
    [property: JsonPropertyName("track_id")] string TrackId,
    [property: JsonPropertyName("published_at")] DateTime PublishedAt
);

public static class MeetingEventTypes
{
    public const string Started = "meeting.started";
    public const string TrackPublished = "meeting.track_published";
    public const string RecordingCompleted = "meeting.recording_completed";
}

public sealed record OutboxEventMessage
{
    [JsonPropertyName("event_id")] public Guid EventId { get; init; }
    [JsonPropertyName("event_type")] public string EventType { get; init; } = string.Empty;
    [JsonPropertyName("schema_version")] public int SchemaVersion { get; init; }
    [JsonPropertyName("occurred_at")] public DateTime OccurredAt { get; init; }
    [JsonPropertyName("producer")] public string Producer { get; init; } = string.Empty;
    [JsonPropertyName("correlation_id")] public string? CorrelationId { get; init; }
    [JsonPropertyName("causation_id")] public string? CausationId { get; init; }
    [JsonPropertyName("workspace_id")] public Guid? WorkspaceId { get; init; }
    [JsonPropertyName("payload_json")] public string PayloadJson { get; init; } = string.Empty;
}

/// <summary>
/// WT-263: one resolved entitlement, with the layer that decided it.
///
/// Provenance is carried on the wire rather than recomputed downstream on purpose. A consumer that
/// only received <c>{key, value}</c> would have to know the resolution order to explain a limit,
/// and knowing the order is one short step from re-deriving it — which is exactly how every
/// service ended up with its own private idea of a plan quota before this ticket.
/// </summary>
public sealed record ResolvedEntitlementPayload(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("source")] string Source
);

/// <summary>
/// WT-263: the complete resolved entitlement map for one workspace, as published by BillingService's
/// EntitlementResolver. This is a FULL snapshot, never a delta — a consumer that misses an event
/// still converges on the next one, and there is no ordering requirement beyond
/// <see cref="ResolvedAt"/>.
/// </summary>
public sealed record EntitlementsChangedEventPayload(
    [property: JsonPropertyName("workspace_id")] Guid WorkspaceId,
    [property: JsonPropertyName("plan_slug")] string? PlanSlug,
    [property: JsonPropertyName("has_active_subscription")] bool HasActiveSubscription,
    [property: JsonPropertyName("resolved_at")] DateTime ResolvedAt,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("entitlements")] IReadOnlyList<ResolvedEntitlementPayload> Entitlements
);

public static class BillingEventTypes
{
    public const string PaymentSucceeded = "billing.payment_succeeded";
    public const string PaymentFailed = "billing.payment_failed";
    public const string PaymentRefunded = "billing.payment_refunded";
    public const string PaymentDisputed = "billing.payment_disputed";
    public const string SubscriptionCancelled = "billing.subscription_cancelled";

    /// <summary>
    /// WT-263. Published whenever a subscription, a plan, a contract override or a workspace
    /// self-service override changes what a workspace may do. Consumers persist the payload as a
    /// local snapshot and enforce against that, never against a live billing call.
    /// </summary>
    public const string EntitlementsChanged = "billing.entitlements_changed";

    /// <summary>
    /// Redis Pub/Sub channel carrying <see cref="EntitlementsChanged"/>. Deliberately its own
    /// channel rather than the billing notification channel: that one is user-facing realtime
    /// chatter fanned out to SignalR, this one is service-to-service state replication.
    /// </summary>
    public const string EntitlementsChangedChannel = "warptalk:entitlements:changed";

    public static string ForStatus(string status) => status switch
    {
        "paid" => PaymentSucceeded,
        "failed" => PaymentFailed,
        "refunded" => PaymentRefunded,
        "disputed" => PaymentDisputed,
        "cancelled" => SubscriptionCancelled,
        _ => $"billing.payment_{status}"
    };
}
