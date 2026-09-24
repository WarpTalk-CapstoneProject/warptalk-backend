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
    /// <summary>A platform account, as acted on from the admin user directory.</summary>
    public const string User = "user";
    /// <summary>A row of the language catalog room validation reads (WT-691). Entity id = the code.</summary>
    public const string SupportedLanguage = "supported_language";
    /// <summary>A workspace's billing subscription: plan, trial, comp and contract entitlements.</summary>
    public const string Subscription = "subscription";
    /// <summary>A billing invoice, as settled by hand from the admin workspace page.</summary>
    public const string Invoice = "invoice";
    /// <summary>An internal note an administrator left on a workspace.</summary>
    public const string WorkspaceNote = "workspace_note";
    /// <summary>A marketplace row of the assistant plugin catalog. Entity id = the plugin's id.</summary>
    public const string Plugin = "plugin";
}

/// <summary>
/// Action verbs recorded by the admin workspace page (ERP-style detail). Every value is at most 30
/// characters: <c>workspace_admin_actions.action</c> is varchar(30), and a longer verb would fail
/// the insert — which, for these actions, means the action itself is refused.
/// </summary>
public static class AdminAuditWorkspaceActions
{
    public const string CreditAdjusted = "credit.adjusted";
    public const string PlanChanged = "subscription.plan_changed";
    public const string TrialExtended = "subscription.trial_extended";
    public const string PeriodComped = "subscription.period_comped";
    public const string EntitlementsOverridden = "entitlements.overridden";
    public const string InvoiceMarkedPaid = "invoice.marked_paid";
    public const string OwnershipTransferred = "ownership.transferred";
    public const string NoticeSent = "notice.sent";
    public const string NoteAdded = "note.added";
    public const string DataExported = "data.exported";

    /// <summary>Every verb above, so a test can hold all of them to the column width.</summary>
    public static readonly string[] All =
    [
        CreditAdjusted, PlanChanged, TrialExtended, PeriodComped, EntitlementsOverridden,
        InvoiceMarkedPaid, OwnershipTransferred, NoticeSent, NoteAdded, DataExported,
    ];

    /// <summary>The width of <c>workspace.workspace_admin_actions.action</c>.</summary>
    public const int MaxLength = 30;
}

/// <summary>Service identifiers used as the audit entry's source.</summary>
public static class AdminAuditSources
{
    public const string WorkspaceService = "workspace-service";
    public const string BillingService = "billing-service";
    public const string TranscriptService = "transcript-service";
    public const string NotificationService = "notification-service";
    public const string AuthService = "auth-service";
    public const string TranslationRoomService = "translation-room-service";
    public const string AssistantService = "assistant-service";
}

/// <summary>Action verbs recorded against a language catalog row (WT-691).</summary>
public static class AdminAuditLanguageActions
{
    public const string Created = "language.created";
    public const string Updated = "language.updated";
    public const string Enabled = "language.enabled";
    public const string Disabled = "language.disabled";
}

/// <summary>
/// Action verbs recorded against a marketplace plugin by a platform admin (/admin/plugins).
/// </summary>
public static class AdminAuditPluginActions
{
    public const string Created = "plugin.created";
    public const string Updated = "plugin.updated";
    public const string Retired = "plugin.retired";
    public const string Reinstated = "plugin.reinstated";
    public const string Deleted = "plugin.deleted";
    public const string OAuthClientSet = "plugin.oauth_client_set";
    public const string ToolsReplaced = "plugin.tools_replaced";
    public const string Rediscovered = "plugin.rediscovered";
}

/// <summary>
/// Action verbs recorded against a platform account.
///
/// Constants rather than literals because the value is persisted and queried: the audit screen
/// filters on it, so a typo in one call site becomes a category of action that never appears in
/// anybody's search.
/// </summary>
public static class AdminAuditUserActions
{
    public const string SessionsRevoked = "user.sessions_revoked";
    public const string Deactivated = "user.deactivated";
    public const string Reactivated = "user.reactivated";
    public const string Unlocked = "user.unlocked";
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
