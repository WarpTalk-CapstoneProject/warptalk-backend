using System.Collections.Generic;

namespace WarpTalk.WorkspaceService.Application.DTOs.Workspace;

public record WorkspaceSettingsDto(
    string DefaultLanguage,
    string Timezone,
    List<string> AllowedTargetLanguages,
    bool VoiceCloningEnabled,
    int MaxActiveRooms,
    int ArtifactRetentionDays,
    List<string> VerifiedDomains,
    bool AllowExternalCollaboration,
    bool RequireVerifiedDomainForInternal,
    AiUsagePolicyDto? AiUsagePolicy,
    bool IsProfanityFilterEnabled,
    int InvitationExpiryDays = 7,
    /// <summary>
    /// The most concurrent rooms this workspace's plan permits, whatever
    /// <see cref="MaxActiveRooms"/> says. A workspace may tighten below it and may never raise
    /// above it (EntitlementConstants.Errors.WorkspaceOverrideLoosens), so when this is the
    /// smaller of the two it — not the stored setting — is what meeting creation enforces.
    ///
    /// Reported so the settings page stops presenting a number that is not in force. A workspace
    /// with an inactive subscription resolves every entitlement to the platform default, which is
    /// 5: an owner reading "Max Active Rooms: 20" and being refused at 5 has no way, from that
    /// screen, to discover that their subscription is the reason.
    ///
    /// Null when the workspace has no entitlement snapshot yet (cold start), where no plan quota
    /// is in force at all and the stored setting is the only rule.
    /// </summary>
    int? MaxActiveRoomsCeiling = null,
    /// <summary>Provenance of the ceiling — <c>plan:enterprise</c>, <c>platform_default</c>, … Null with the ceiling.</summary>
    string? MaxActiveRoomsCeilingSource = null,
    bool AllowAnyPlugins = true,
    /// <summary>
    /// How many target languages this workspace's plan permits IN ONE MEETING. WT-500.
    ///
    /// Not a cap on <see cref="AllowedTargetLanguages"/>, and deliberately not turned into one:
    /// the allowlist says which languages a meeting may choose FROM, and the plan says how many it
    /// may choose AT ONCE. A workspace permitting six and running three-language meetings is a
    /// coherent configuration, so clamping the list would take away something nobody was misusing.
    ///
    /// What was wrong is that the quota was invisible until it fired. It is enforced at meeting
    /// creation (WorkspaceDirectoryService.ValidatePlanLanguageQuota), so an owner who enabled six
    /// languages here got no warning at all and then a refusal at the point of creating a meeting —
    /// with nothing on the settings screen connecting the two. That is the same defect
    /// MaxActiveRoomsCeiling above was added to fix, one field across.
    ///
    /// Resolved through <c>Limit</c>, NOT <c>SelfServiceLimit</c>, because that is the function
    /// meeting creation calls. Reporting a ceiling stricter than the one actually applied would be
    /// a new version of the same lie.
    ///
    /// Null when no plan quota is in force — cold start, or no live subscription.
    /// </summary>
    int? MaxLanguagesCeiling = null,
    /// <summary>Provenance of the language ceiling. Null with the ceiling.</summary>
    string? MaxLanguagesCeilingSource = null,
    /// <summary>
    /// WT-646: which plugin keys this workspace permits. Owner and Admin may both set it.
    ///
    /// NULL IS NOT AN EMPTY ALLOWLIST. Null means no allowlist was ever configured, and the
    /// decision falls back to <see cref="AllowAnyPlugins"/> — which is what every workspace that
    /// predates this field looks like, and the only reading under which none of them change
    /// behaviour. An empty list is a configured allowlist that permits nothing. Collapsing the two
    /// would turn a backward-compatible addition into a silent workspace-wide plugin outage.
    ///
    /// Not validated against the plugin catalog, and deliberately not: the catalog is
    /// AssistantService's table in AssistantService's database, and this service has no read path
    /// to it. A key that no plugin claims is therefore accepted here and simply matches nothing
    /// downstream. Faking the check — hard-coding known keys, or calling out to the assistant on
    /// the settings-save path — would either go stale on every catalog change or make saving
    /// workspace settings fail when an unrelated service is down.
    /// </summary>
    List<string>? AllowedPluginKeys = null,
    /// <summary>
    /// Whether a plain Member may install a plugin for themselves; false confines installation to
    /// Owner and Admin. Owner and Admin may both set it. True by default, because that is what
    /// every workspace does today.
    /// </summary>
    bool AllowMemberPluginInstall = true,
    /// <summary>
    /// Whether an installed plugin needs Owner/Admin approval before it may be invoked.
    ///
    /// OWNER-ONLY to change, gated alongside <see cref="AllowExternalCollaboration"/> in
    /// <c>UpdateWorkspaceSettingsAsync</c> rather than with the other two plugin fields. It is the
    /// one that can take away a capability an Admin currently has, so an Admin must not be able to
    /// switch it off — nor, symmetrically, to switch it on and gate the Owner's own installs.
    /// </summary>
    bool RequirePluginApproval = false
);

public record WorkspaceSettingsPatchRequest(
    string? DefaultLanguage = null,
    string? Timezone = null,
    List<string>? AllowedTargetLanguages = null,
    bool? VoiceCloningEnabled = null,
    int? MaxActiveRooms = null,
    int? ArtifactRetentionDays = null,
    List<string>? VerifiedDomains = null,
    bool? AllowExternalCollaboration = null,
    bool? RequireVerifiedDomainForInternal = null,
    AiUsagePolicyPatchDto? AiUsagePolicy = null,
    bool? IsProfanityFilterEnabled = null,
    bool? AllowAnyPlugins = null,
    /// <summary>
    /// WT-646. Null here means "not supplied by this patch", NOT the DTO's null-means-fall-back-to
    /// -AllowAnyPlugins. The two readings collide, so this shape cannot express "clear the
    /// allowlist back to unset" — send the full document to PUT for that. Left as-is rather than
    /// wrapped in an extra presence type because nothing calls this: the live PATCH endpoint
    /// merges a raw JsonObject instead (WorkspacesController.PatchWorkspaceSettings), and this
    /// record and WorkspaceMapper.ApplyPatch are currently unreferenced.
    /// </summary>
    List<string>? AllowedPluginKeys = null,
    bool? AllowMemberPluginInstall = null,
    bool? RequirePluginApproval = null
);

public record AiUsagePolicyPatchDto(
    bool? AllowExternalLlm = null,
    PiiRedactionDto? RedactPii = null,
    DlpDto? Dlp = null,
    TranslationProfileDto? TranslationProfile = null,
    bool? UseGlobalGlossary = null
);
