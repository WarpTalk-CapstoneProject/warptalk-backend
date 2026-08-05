namespace WarpTalk.BillingService.Domain.Constants;

/// <summary>
/// WT-263: the entitlement vocabulary. Layer ③ of the model — the layer that did not exist, which
/// is why every plan column was dead code and every service either re-derived a quota or gave up.
///
/// Everything an entitlement can be is named here: its key, the platform default it falls back to,
/// and the provenance labels a resolved value can carry. Adding a capability means adding a key
/// here and a case in EntitlementResolver — nowhere else.
/// </summary>
public static class EntitlementConstants
{
    /// <summary>
    /// Entitlement keys. These are wire values: they travel in billing.entitlements_changed and are
    /// persisted verbatim in every consumer's snapshot table, so renaming one is a breaking change.
    /// </summary>
    public static class Keys
    {
        public const string MaxLanguages = "max_languages";
        public const string MaxActiveRooms = "max_active_rooms";
        public const string MaxParticipants = "max_participants";
        public const string VoiceClone = "voice_clone";
        public const string AiAssistant = "ai_assistant";
        public const string Glossary = "glossary";

        /// <summary>
        /// Every key the resolver produces. The contract test pins this set: a key added without
        /// updating the expected matrix fails CI rather than shipping an unenforced capability.
        /// </summary>
        public static readonly string[] All =
        [
            MaxLanguages,
            MaxActiveRooms,
            MaxParticipants,
            VoiceClone,
            AiAssistant,
            Glossary
        ];

        /// <summary>
        /// Keys whose value is a numeric limit, where a SMALLER number is the tighter setting.
        /// Everything not listed here is a boolean capability, where <c>false</c> is tighter.
        /// The tighten-not-loosen invariant needs to know which direction "tighter" points.
        /// </summary>
        public static readonly string[] NumericLimits =
        [
            MaxLanguages,
            MaxActiveRooms,
            MaxParticipants
        ];

        public static bool IsNumericLimit(string key) =>
            Array.Exists(NumericLimits, limit => string.Equals(limit, key, StringComparison.Ordinal));
    }

    /// <summary>
    /// Provenance labels. <c>plan:</c> is a prefix — the full source for a plan-decided value is
    /// <c>plan:enterprise</c>, so a support answer names the row that decided the limit.
    /// </summary>
    public static class Sources
    {
        public const string PlatformDefault = "platform_default";
        public const string PlanPrefix = "plan:";
        public const string ContractOverride = "contract_override";
        public const string WorkspaceOverride = "workspace_override";

        public static string Plan(string? planSlug) =>
            PlanPrefix + (string.IsNullOrWhiteSpace(planSlug) ? "unknown" : planSlug);
    }

    /// <summary>
    /// What a workspace gets when NOTHING above it has an opinion — no plan row, no contract term,
    /// no self-service setting. These are the floor of the resolution order, not a fallback for an
    /// error: an outage never lands here, because enforcement reads a local snapshot instead.
    /// </summary>
    public static class PlatformDefaults
    {
        /// <summary>Mirrors <see cref="SubscriptionConstants.PlanDefaults.MaxLanguages"/> and the
        /// <c>subscription.plans.max_languages</c> column default.</summary>
        public const int MaxLanguages = SubscriptionConstants.PlanDefaults.MaxLanguages;

        /// <summary>Mirrors <see cref="SubscriptionConstants.PlanDefaults.MaxParticipants"/>.</summary>
        public const int MaxParticipants = SubscriptionConstants.PlanDefaults.MaxParticipants;

        /// <summary>
        /// 5 — the same number WorkspaceService has used as its settings-JSON default
        /// (<c>WorkspaceConstants.DefaultWorkspaceMaxActiveRooms</c>) since before this ticket, and
        /// the default of the new <c>subscription.plans.max_active_rooms</c> column. Keeping the
        /// three in step is what lets the backfill treat a stored 5 as "never chosen".
        /// </summary>
        public const int MaxActiveRooms = 5;

        public const bool VoiceClone = false;
        public const bool AiAssistant = false;
        public const bool Glossary = false;
    }

    /// <summary>Reason strings stamped on a published event, for operators reading the outbox.</summary>
    public static class Reasons
    {
        public const string SubscriptionChanged = "subscription_changed";
        public const string PlanChanged = "plan_changed";
        public const string ContractOverrideChanged = "contract_override_changed";
        public const string WorkspaceOverrideChanged = "workspace_override_changed";
        public const string Backfill = "backfill";
    }

    public static class Errors
    {
        public const string UnknownEntitlementKey = "Unknown entitlement key '{0}'.";

        /// <summary>
        /// The tighten-not-loosen rejection. A workspace owner may always restrict their own
        /// workspace further than the plan allows; raising the limit is a purchase, not a setting.
        /// </summary>
        public const string WorkspaceOverrideLoosens =
            "Workspace setting '{0}' cannot exceed what the plan allows ({1}). A workspace may only tighten its own limits.";
    }

    /// <summary>Producer name stamped on the outbox envelope.</summary>
    public const string Producer = "billing-service";
}
