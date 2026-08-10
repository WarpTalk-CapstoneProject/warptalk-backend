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
    int InvitationExpiryDays = 7
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
