using System.Collections.Generic;
using WarpTalk.WorkspaceService.Domain.Constants;

namespace WarpTalk.WorkspaceService.Domain.Settings;

public class WorkspaceConfiguration
{
    private string _defaultLanguage = WorkspaceConstants.DefaultWorkspaceLanguage;
    private string _timezone = WorkspaceConstants.DefaultWorkspaceTimezone;
    private List<string> _allowedTargetLanguages = new();
    private bool? _restrictLanguages;
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

    /// <summary>
    /// Whether <see cref="AllowedTargetLanguages"/> is a whitelist at all — the "Allow all
    /// languages" switch, inverted (WT-706).
    ///
    /// WHY A SEPARATE FLAG. The list alone cannot tell "this workspace permits every language"
    /// from "this workspace permits none", and every reader had to pick one: empty means
    /// unrestricted. That reading is right for a workspace that never configured languages, and
    /// it made unticking the LAST language in a restricted workspace silently switch the whole
    /// tenant to unrestricted — the opposite of what the owner was doing. With this flag the two
    /// states are distinct, and a restricted workspace that empties its list is refused on save
    /// instead (WorkspaceSettingsValidator).
    ///
    /// BACK-COMPAT. Absent from every settings JSON written before WT-706, so it derives from the
    /// stored list: a non-empty list was restricted, an empty one was not. That is exactly what
    /// the old readers concluded, so existing data keeps its meaning without a data migration.
    /// The derived value is what gets serialized, so the flag becomes explicit on the next save.
    /// </summary>
    public bool RestrictLanguages
    {
        get => _restrictLanguages ?? _allowedTargetLanguages.Count > 0;
        set => _restrictLanguages = value;
    }

    /// <summary>
    /// The whitelist, normalized to lower-case primary subtags ("vi", not "vi-VN") on save.
    ///
    /// EMPTY STILL MEANS UNRESTRICTED ON THE WIRE, and deliberately so: the gRPC settings
    /// response, translation-room's room-edit gate and the gateway's RoomLanguagePolicy all read
    /// it that way, and teaching every consumer about <see cref="RestrictLanguages"/> would be a
    /// four-service change for no behaviour difference. Instead the getter keeps the invariant
    /// those consumers rely on — no restriction, no entries — whatever a stale document holds.
    /// </summary>
    public List<string> AllowedTargetLanguages
    {
        get => RestrictLanguages ? _allowedTargetLanguages : new List<string>();
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

    /// <summary>Workspace policy for invoking account-scoped MCP plugins in WarpBot.</summary>
    public bool AllowAnyPlugins { get; set; } = true;

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
