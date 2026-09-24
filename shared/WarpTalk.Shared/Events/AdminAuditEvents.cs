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
    /// <summary>A sellable plan (billing <c>plans</c>): name, price, credits and quotas.</summary>
    public const string Plan = "plan";
    /// <summary>The platform billing policy (today the VAT rate).</summary>
    public const string BillingPolicy = "billing_policy";
    /// <summary>The platform pricing configuration (markup, FX, credit value).</summary>
    public const string PricingConfig = "pricing_config";
    /// <summary>An inbound enterprise sales enquiry.</summary>
    public const string SalesLead = "sales_lead";
    /// <summary>The platform audit log itself — read out of the portal as an export.</summary>
    public const string AuditLog = "audit_log";
    /// <summary>A payment recorded by hand against a subscription (bank transfer, offline).</summary>
    public const string Payment = "payment";
    /// <summary>The USD→VND rate VND reports convert with (billing <c>fx_rates</c> + its config keys).</summary>
    public const string FxRate = "fx_rate";
    /// <summary>A platform staff member (G10). Entity id = the staff member's USER id.</summary>
    public const string StaffMember = "staff_member";
    /// <summary>A platform staff role and its permissions (G10). Entity id = auth.roles.id.</summary>
    public const string StaffRole = "staff_role";
    /// <summary>Staff access offered to an address with no account yet (G10).</summary>
    public const string StaffInvitation = "staff_invitation";

    /// <summary>
    /// Every value above, so the audit screen's entity filter and the web's label table have one
    /// list to agree with.
    /// </summary>
    public static readonly string[] All =
    [
        Workspace, CreditAdjustment, PricingVersion, UsageRate, PaymentMethod, GlossaryTerm,
        Notification, User, SupportedLanguage, Subscription, Invoice, WorkspaceNote, Plugin, Plan,
        BillingPolicy, PricingConfig, SalesLead, AuditLog, Payment, FxRate,
        StaffMember, StaffRole, StaffInvitation,
    ];
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

/// <summary>
/// Verbs for the platform billing and catalog writes that predate the admin workspace page:
/// plans, rate cards, pricing, VAT, contracts, the /admin/subscriptions lifecycle buttons and the
/// sales-lead inbox. Recorded by <c>WarpTalk.Shared.AdminAudit</c> rather than by hand.
/// Every value fits <see cref="AdminAuditWorkspaceActions.MaxLength"/>.
/// </summary>
public static class AdminAuditBillingActions
{
    public const string PlanCreated = "plan.created";
    public const string PlanUpdated = "plan.updated";
    public const string RateCardUpserted = "rate_card.upserted";
    public const string RateCardDeactivated = "rate_card.deactivated";
    public const string RateCardCostSet = "rate_card.provider_cost_set";
    public const string PricingConfigUpdated = "pricing_config.updated";
    public const string BillingPolicyUpdated = "billing_policy.updated";
    public const string ContractCreated = "subscription.contract_created";
    public const string ContractTermsUpdated = "subscription.contract_terms";
    public const string SubscriptionCancelled = "subscription.cancelled";
    public const string SubscriptionReactivated = "subscription.reactivated";
    public const string SubscriptionResumed = "subscription.resumed";
    public const string SalesLeadStatusChanged = "sales_lead.status_changed";
    public const string PaymentRecorded = "payment.recorded";
    public const string FxRateRefreshed = "fx_rate.refreshed";
    public const string FxRateOverridden = "fx_rate.overridden";
    public const string FxRateOverrideCleared = "fx_rate.override_cleared";

    public static readonly string[] All =
    [
        PlanCreated, PlanUpdated, RateCardUpserted, RateCardDeactivated, RateCardCostSet,
        PricingConfigUpdated, BillingPolicyUpdated, ContractCreated, ContractTermsUpdated,
        SubscriptionCancelled, SubscriptionReactivated, SubscriptionResumed, SalesLeadStatusChanged,
        PaymentRecorded, FxRateRefreshed, FxRateOverridden, FxRateOverrideCleared,
    ];
}

/// <summary>Verbs for the platform-wide glossary (transcript service, /admin/global-glossary).</summary>
public static class AdminAuditGlossaryActions
{
    public const string TermCreated = "glossary.term_created";
    public const string TermUpdated = "glossary.term_updated";
    public const string TermDeleted = "glossary.term_deleted";
    public const string TermPublished = "glossary.term_published";
    public const string TermArchived = "glossary.term_archived";
    public const string BulkImported = "glossary.bulk_imported";

    public static readonly string[] All =
        [TermCreated, TermUpdated, TermDeleted, TermPublished, TermArchived, BulkImported];
}

/// <summary>Verbs for platform announcements (notification service, /admin/announcements).</summary>
public static class AdminAuditAnnouncementActions
{
    public const string Sent = "announcement.sent";

    public static readonly string[] All = [Sent];
}

/// <summary>Verbs the audit screen records about itself.</summary>
public static class AdminAuditLogActions
{
    public const string Exported = "audit_log.exported";

    public static readonly string[] All = [Exported];
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

    // Per-workspace availability. Recorded with the workspace's id, so the workspace's own audit
    // trail shows who turned a plugin on or off for it, and why.

    /// <summary>The platform default (available / opt-in / retired) or the plan rule changed.</summary>
    public const string AvailabilitySet = "plugin.availability_set";
    public const string WorkspaceEnabled = "plugin.workspace_enabled";
    public const string WorkspaceDisabled = "plugin.workspace_disabled";
    /// <summary>A workspace override removed: the workspace follows the platform default again.</summary>
    public const string WorkspaceReset = "plugin.workspace_reset";
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

/// <summary>
/// Verbs for platform staff and roles (G10, /admin/staff and /admin/roles). Recorded by the auth
/// service before each change commits; an unrecorded staff change is abandoned.
/// </summary>
public static class AdminAuditStaffActions
{
    /// <summary>An existing account was given staff access directly.</summary>
    public const string Granted = "staff.granted";
    /// <summary>An address with no account was invited.</summary>
    public const string Invited = "staff.invited";
    public const string InvitationRevoked = "staff.invitation_revoked";
    /// <summary>The invitee signed in with the invited, verified address. Actor = the invitee.</summary>
    public const string InvitationAccepted = "staff.invitation_accepted";
    public const string RoleChanged = "staff.role_changed";
    public const string Suspended = "staff.suspended";
    public const string Reactivated = "staff.reactivated";
    public const string Removed = "staff.removed";
    public const string RoleCreated = "staff_role.created";
    public const string RoleUpdated = "staff_role.updated";
    public const string RoleDuplicated = "staff_role.duplicated";
    public const string RoleDeleted = "staff_role.deleted";

    public static readonly string[] All =
    [
        Granted, Invited, InvitationRevoked, InvitationAccepted, RoleChanged, Suspended, Reactivated,
        Removed, RoleCreated, RoleUpdated, RoleDuplicated, RoleDeleted,
    ];
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
    [property: JsonPropertyName("after_summary")] IReadOnlyDictionary<string, string?>? AfterSummary)
{
    // Optional context, added after the positional contract shipped. Init-only so every existing
    // constructor call keeps compiling and an older publisher's JSON still deserializes.

    /// <summary>The actor's e-mail as the token carried it — a snapshot, the id stays the identity.</summary>
    [JsonPropertyName("actor_email")] public string? ActorEmail { get; init; }

    [JsonPropertyName("actor_name")] public string? ActorName { get; init; }

    /// <summary>A subject whose natural key is not a GUID (a language code, a plugin key).</summary>
    [JsonPropertyName("entity_key")] public string? EntityKey { get; init; }

    /// <summary>What the subject was called at the time.</summary>
    [JsonPropertyName("entity_label")] public string? EntityLabel { get; init; }

    /// <summary>Why a failed entry failed. Null on success.</summary>
    [JsonPropertyName("error_message")] public string? ErrorMessage { get; init; }

    /// <summary>The admin's address as the gateway forwarded it.</summary>
    [JsonPropertyName("ip_address")] public string? IpAddress { get; init; }

    [JsonPropertyName("user_agent")] public string? UserAgent { get; init; }
}
