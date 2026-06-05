using System.Collections.Generic;
using WarpTalk.WorkspaceService.Domain.Constants;

namespace WarpTalk.WorkspaceService.Domain.Settings;

public class WorkspaceConfiguration
{
    private string _defaultLanguage = WorkspaceConstants.DefaultWorkspaceLanguage;
    private string _timezone = WorkspaceConstants.DefaultWorkspaceTimezone;
    private List<string> _allowedTargetLanguages = new();
    private int _maxActiveRooms = WorkspaceConstants.DefaultWorkspaceMaxActiveRooms;
    private int _artifactRetentionDays = WorkspaceConstants.DefaultWorkspaceArtifactRetentionDays;

    // 1. Localization & General
    public string DefaultLanguage
    {
        get => _defaultLanguage;
        set => _defaultLanguage = string.IsNullOrWhiteSpace(value) ? WorkspaceConstants.DefaultWorkspaceLanguage : value;
    }

    public string Timezone
    {
        get => _timezone;
        set => _timezone = string.IsNullOrWhiteSpace(value) ? WorkspaceConstants.DefaultWorkspaceTimezone : value;
    }

    // 2. Translation & Audio Policies
    public List<string> AllowedTargetLanguages
    {
        get => _allowedTargetLanguages;
        set => _allowedTargetLanguages = value ?? new List<string>();
    }

    public bool VoiceCloningEnabled { get; set; } = true;

    public int MaxActiveRooms
    {
        get => _maxActiveRooms;
        set => _maxActiveRooms = value <= 0 ? WorkspaceConstants.DefaultWorkspaceMaxActiveRooms : value;
    }

    // 3. Security & Artifact Retention
    public int ArtifactRetentionDays
    {
        get => _artifactRetentionDays;
        set => _artifactRetentionDays = value <= 0 ? WorkspaceConstants.DefaultWorkspaceArtifactRetentionDays : value;
    }

    public bool EnforceHostApprovalDefault { get; set; } = true;

    // 4. Enterprise & External Collaboration
    public List<string> VerifiedDomains { get; set; } = new();
    public bool AllowExternalCollaboration { get; set; } = true;
    public int? ExternalGracePeriodHours { get; set; }
}
