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

public static class BillingEventTypes
{
    public const string PaymentSucceeded = "billing.payment_succeeded";
    public const string PaymentFailed = "billing.payment_failed";
    public const string PaymentRefunded = "billing.payment_refunded";
    public const string PaymentDisputed = "billing.payment_disputed";
    public const string SubscriptionCancelled = "billing.subscription_cancelled";

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
