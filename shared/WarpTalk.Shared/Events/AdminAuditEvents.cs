using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace WarpTalk.Shared.Events;

public static class AdminAuditEventTypes
{
    public const string ActionRecorded = "admin.action_recorded";
}

/// <summary>
/// Subject of an administrative action. The value is persisted, so treat these as a contract.
/// </summary>
public static class AdminAuditEntityTypes
{
    public const string Workspace = "workspace";
    public const string CreditAdjustment = "credit_adjustment";
    public const string PricingVersion = "pricing_version";
    public const string UsageRate = "usage_rate";
    public const string PaymentMethod = "payment_method";
    public const string GlossaryTerm = "glossary_term";
    public const string Notification = "notification";
}

/// <summary>Service identifiers used as the audit entry's source.</summary>
public static class AdminAuditSources
{
    public const string WorkspaceService = "workspace-service";
    public const string BillingService = "billing-service";
    public const string TranscriptService = "transcript-service";
    public const string NotificationService = "notification-service";
}

public static class AdminAuditResults
{
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
}

/// <summary>
/// Published by any service that performs an administrative mutation, consumed by the workspace
/// service which owns the append-only audit store (WT-210).
///
/// Services keep their own logical databases, so this is how an action taken in billing or
/// transcript becomes queryable next to a workspace suspension without anyone writing across a
/// database boundary.
/// </summary>
/// <param name="BeforeSummary">
/// Safe, human-readable summary of prior state. Must already be redacted by the publisher —
/// the consumer redacts again defensively, but a secret should never reach the bus.
/// </param>
public sealed record AdminActionRecordedEvent(
    [property: JsonPropertyName("source_service")] string SourceService,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("entity_type")] string EntityType,
    [property: JsonPropertyName("entity_id")] Guid? EntityId,
    [property: JsonPropertyName("workspace_id")] Guid? WorkspaceId,
    [property: JsonPropertyName("actor_id")] Guid ActorId,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("result")] string Result,
    [property: JsonPropertyName("performed_at")] DateTime PerformedAt,
    [property: JsonPropertyName("correlation_id")] string? CorrelationId,
    [property: JsonPropertyName("before_summary")] IReadOnlyDictionary<string, string?>? BeforeSummary,
    [property: JsonPropertyName("after_summary")] IReadOnlyDictionary<string, string?>? AfterSummary);
