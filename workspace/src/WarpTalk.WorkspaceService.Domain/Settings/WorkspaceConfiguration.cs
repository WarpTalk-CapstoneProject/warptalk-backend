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

    // 3. Security & Artifact Retention
    public int ArtifactRetentionDays
    {
        get => _artifactRetentionDays;
        set => _artifactRetentionDays = value < 0 ? WorkspaceConstants.DefaultWorkspaceArtifactRetentionDays : value;
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

    public bool EnforceHostApprovalDefault { get; set; } = true;

    // 4. Enterprise & External Collaboration
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
