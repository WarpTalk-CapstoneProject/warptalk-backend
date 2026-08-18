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
    private int _invitationExpiryDays = WorkspaceConstants.DefaultInvitationExpiryDays;
    private AiUsagePolicyConfiguration? _aiUsagePolicy = NormalizeAiUsagePolicy(null);

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

    public int ArtifactRetentionDays
    {
        get => _artifactRetentionDays;
        set => _artifactRetentionDays = value < WorkspaceConstants.MinWorkspaceArtifactRetentionDays
            ? WorkspaceConstants.DefaultWorkspaceArtifactRetentionDays
            : value;
    }

    public int InvitationExpiryDays
    {
        get => _invitationExpiryDays;
        set => _invitationExpiryDays = value switch
        {
            < WorkspaceConstants.MinWorkspaceInvitationExpiryDays => WorkspaceConstants.DefaultInvitationExpiryDays,
            > WorkspaceConstants.MaxWorkspaceInvitationExpiryDays => WorkspaceConstants.MaxWorkspaceInvitationExpiryDays,
            _ => value
        };
    }


    // 4. Enterprise & External Collaboration

    /// <summary>
    /// Display mirror of <c>workspace.workspace_verified_domains</c>. Read it to render a list;
    /// never to decide anything. The table is the only record — domains are added and revoked
    /// through VerifiedDomainService, which does not write this JSON, so a stored copy is only as
    /// fresh as the last settings save. Treating it as policy is what let revoked domains go on
    /// granting Internal membership (WT-179). Every decision reads
    /// <c>WorkspaceHelper.GetActiveVerifiedDomainsAsync</c> instead.
    /// </summary>
    public List<string> VerifiedDomains { get; set; } = new();
    public bool AllowExternalCollaboration { get; set; } = true;
    // Verification is opt-in for a new workspace without an explicit domain.
    // Keep this aligned with CreateWorkspaceAsync and the FE default.
    public bool RequireVerifiedDomainForInternal { get; set; } = false;
    public int? ExternalGracePeriodHours { get; set; }

    // 5. AI Ingestion & Security Guardrails
    public AiUsagePolicyConfiguration? AiUsagePolicy
    {
        get => _aiUsagePolicy;
        set => _aiUsagePolicy = NormalizeAiUsagePolicy(value);
    }

    // 6. Content Filtering
    public bool IsProfanityFilterEnabled { get; set; } = false;

    private static AiUsagePolicyConfiguration NormalizeAiUsagePolicy(AiUsagePolicyConfiguration? value)
    {
        return value == null
            ? new AiUsagePolicyConfiguration(
                AllowExternalLlm: true,
                RedactPii: new PiiRedactionConfiguration(Enabled: true),
                Dlp: new DlpConfiguration(Enabled: false, KeywordsBlacklist: new List<string>()),
                TranslationProfile: new TranslationProfileConfiguration(
                    TranslationTone: "professional",
                    LanguageSpecificRules: new LanguageSpecificRules(
                        VietnameseHonorificStyle: "formal_hierarchical",
                        JapaneseHonorificStyle: "keigo_teineigo")))
            : value with { AllowExternalLlm = true };
    }
}
