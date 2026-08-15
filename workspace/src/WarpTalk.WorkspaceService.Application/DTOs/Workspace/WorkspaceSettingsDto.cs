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
    string? MaxActiveRoomsCeilingSource = null
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
    bool? IsProfanityFilterEnabled = null
);

public record AiUsagePolicyPatchDto(
    bool? AllowExternalLlm = null,
    PiiRedactionDto? RedactPii = null,
    DlpDto? Dlp = null,
    TranslationProfileDto? TranslationProfile = null,
    bool? UseGlobalGlossary = null
);
