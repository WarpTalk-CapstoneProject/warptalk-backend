using System.Collections.Generic;

namespace WarpTalk.AuthService.Application.DTOs;

public record WorkspaceSettingsDto(
    string DefaultLanguage,
    string Timezone,
    List<string> AllowedTargetLanguages,
    bool VoiceCloningEnabled,
    int MaxActiveRooms,
    int ArtifactRetentionDays,
    bool EnforceHostApprovalDefault,
    List<string> VerifiedDomains,
    bool AllowExternalCollaboration
);
