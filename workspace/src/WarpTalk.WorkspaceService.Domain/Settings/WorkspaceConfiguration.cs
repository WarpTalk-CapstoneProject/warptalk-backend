using System;
using System.Collections.Generic;
using System.Linq;
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
    private List<string>? _allowedPluginKeys;

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

    /// <summary>Workspace policy for invoking account-scoped MCP plugins in WarpBot.</summary>
    public bool AllowAnyPlugins { get; set; } = true;

    /// <summary>
    /// WT-646: the workspace's plugin allowlist, by plugin key.
    ///
    /// NULL AND EMPTY MEAN OPPOSITE THINGS, and the distinction is the whole backward-compatibility
    /// story. Null is "this workspace never configured an allowlist" — every workspace in existence
    /// before this field, and the only reading under which they keep behaving as they do today —
    /// so the decision falls back to <see cref="AllowAnyPlugins"/> alone. An empty list is a
    /// deliberate allowlist that permits nothing. Anything that collapses the two (a
    /// <c>?? new List&lt;string&gt;()</c>, a proto3 repeated field with no presence flag) silently
    /// turns every existing workspace into one that permits no plugins at all.
    ///
    /// Normalized on write rather than in the validator: entries are trimmed and de-duplicated
    /// case-insensitively here so the stored JSON is canonical no matter which path wrote it.
    /// Blank entries and an over-long list are still REJECTED by WorkspaceSettingsValidator rather
    /// than quietly dropped here — silently discarding part of a security policy the caller asked
    /// for is worse than refusing the whole request.
    /// </summary>
    public List<string>? AllowedPluginKeys
    {
        get => _allowedPluginKeys;
        set => _allowedPluginKeys = value is null
            ? null
            : value
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Select(key => key.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
    }

    /// <summary>
    /// Whether a plain Member may install a plugin for themselves. False confines installation to
    /// Owners and Admins. Defaults to true: that is what every workspace does today, and this
    /// field must not change behaviour for anyone who has not set it.
    /// </summary>
    public bool AllowMemberPluginInstall { get; set; } = true;

    /// <summary>
    /// Whether an installed plugin needs an Owner/Admin approval before it can be invoked.
    /// Defaults to false for the same reason as above — an unset workspace keeps working.
    ///
    /// Owner-only to change (WorkspaceService.UpdateWorkspaceSettingsAsync), unlike the two fields
    /// above. It is the one that can be used to lock a workspace's Admins out of a capability they
    /// currently hold, so it sits with AllowExternalCollaboration on the owner-only side of the
    /// line rather than with the rest of the plugin settings.
    /// </summary>
    public bool RequirePluginApproval { get; set; } = false;

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
