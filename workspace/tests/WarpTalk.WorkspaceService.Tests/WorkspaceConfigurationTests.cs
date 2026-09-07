using System.Collections.Generic;
using System.Text.Json;
using Xunit;
using WarpTalk.WorkspaceService.Domain.Settings;
using WarpTalk.WorkspaceService.Domain.Constants;

namespace WarpTalk.WorkspaceService.Tests;

public class WorkspaceConfigurationTests
{
    [Fact]
    public void WorkspaceConfiguration_ShouldDefaultToSafeValues_WhenInstantiated()
    {
        // Act
        var config = new WorkspaceConfiguration();

        // Assert
        Assert.Equal(WorkspaceConstants.DefaultWorkspaceLanguage, config.DefaultLanguage);
        Assert.Equal(WorkspaceConstants.DefaultWorkspaceTimezone, config.Timezone);
        Assert.NotNull(config.AllowedTargetLanguages);
        Assert.Empty(config.AllowedTargetLanguages);
        Assert.Equal(WorkspaceConstants.DefaultWorkspaceMaxActiveRooms, config.MaxActiveRooms);
        Assert.Equal(WorkspaceConstants.DefaultWorkspaceArtifactRetentionDays, config.ArtifactRetentionDays);
        Assert.Equal(WorkspaceConstants.DefaultInvitationExpiryDays, config.InvitationExpiryDays);
        Assert.NotNull(config.AiUsagePolicy);
        Assert.True(config.AiUsagePolicy.AllowExternalLlm);
        Assert.NotNull(config.AiUsagePolicy.RedactPii);
        Assert.True(config.AiUsagePolicy.RedactPii.Enabled);
        Assert.NotNull(config.AiUsagePolicy.Dlp);
        Assert.False(config.AiUsagePolicy.Dlp.Enabled);
        Assert.NotNull(config.AiUsagePolicy.Dlp.KeywordsBlacklist);
        Assert.Empty(config.AiUsagePolicy.Dlp.KeywordsBlacklist);
        Assert.NotNull(config.AiUsagePolicy.TranslationProfile);
        Assert.Equal("professional", config.AiUsagePolicy.TranslationProfile.TranslationTone);
    }

    [Fact]
    public void WorkspaceConfiguration_ShouldApplyDefaultsAndNormalize_WhenDeserializedFromNullOrInvalidJson()
    {
        // Arrange
        var json = "{\"DefaultLanguage\":null,\"Timezone\":\"   \",\"AllowedTargetLanguages\":null,\"MaxActiveRooms\":-5,\"ArtifactRetentionDays\":-1}";

        // Act
        var config = JsonSerializer.Deserialize<WorkspaceConfiguration>(json);

        // Assert
        Assert.NotNull(config);
        Assert.Equal(WorkspaceConstants.DefaultWorkspaceLanguage, config.DefaultLanguage);
        Assert.Equal(WorkspaceConstants.DefaultWorkspaceTimezone, config.Timezone);
        Assert.NotNull(config.AllowedTargetLanguages);
        Assert.Empty(config.AllowedTargetLanguages);
        Assert.Equal(WorkspaceConstants.DefaultWorkspaceMaxActiveRooms, config.MaxActiveRooms);
        Assert.Equal(WorkspaceConstants.DefaultWorkspaceArtifactRetentionDays, config.ArtifactRetentionDays);
        Assert.NotNull(config.AiUsagePolicy);
        Assert.True(config.AiUsagePolicy.AllowExternalLlm);
        Assert.NotNull(config.AiUsagePolicy.RedactPii);
        Assert.True(config.AiUsagePolicy.RedactPii.Enabled);
    }

    [Fact]
    public void WorkspaceConfiguration_ShouldNormalizeInvitationExpiryDays_WhenDeserialized()
    {
        var invalidLow = JsonSerializer.Deserialize<WorkspaceConfiguration>(
            "{\"InvitationExpiryDays\":0}");
        var invalidHigh = JsonSerializer.Deserialize<WorkspaceConfiguration>(
            "{\"InvitationExpiryDays\":366}");
        var valid = JsonSerializer.Deserialize<WorkspaceConfiguration>(
            "{\"InvitationExpiryDays\":30}");

        Assert.NotNull(invalidLow);
        Assert.NotNull(invalidHigh);
        Assert.NotNull(valid);
        Assert.Equal(WorkspaceConstants.DefaultInvitationExpiryDays, invalidLow.InvitationExpiryDays);
        Assert.Equal(WorkspaceConstants.MaxWorkspaceInvitationExpiryDays, invalidHigh.InvitationExpiryDays);
        Assert.Equal(30, valid.InvitationExpiryDays);
    }

    [Fact]
    public void WorkspaceConfiguration_ShouldNormalizeZeroRetentionToDefault()
    {
        var config = JsonSerializer.Deserialize<WorkspaceConfiguration>(
            "{\"ArtifactRetentionDays\":0}");

        Assert.NotNull(config);
        Assert.Equal(WorkspaceConstants.DefaultWorkspaceArtifactRetentionDays, config.ArtifactRetentionDays);
    }

    [Fact]
    public void WorkspaceConfiguration_ShouldRetainValues_WhenDeserializedFromValidJson()
    {
        // Arrange
        var json = "{\"DefaultLanguage\":\"vi\",\"Timezone\":\"Asia/Ho_Chi_Minh\",\"AllowedTargetLanguages\":[\"en\",\"vi\"],\"VoiceCloningEnabled\":false,\"MaxActiveRooms\":10,\"ArtifactRetentionDays\":15}";

        // Act
        var config = JsonSerializer.Deserialize<WorkspaceConfiguration>(json);

        // Assert
        Assert.NotNull(config);
        Assert.Equal("vi", config.DefaultLanguage);
        Assert.Equal("Asia/Ho_Chi_Minh", config.Timezone);
        Assert.NotNull(config.AllowedTargetLanguages);
        Assert.Equal(new List<string> { "en", "vi" }, config.AllowedTargetLanguages);
        Assert.False(config.VoiceCloningEnabled);
        Assert.Equal(10, config.MaxActiveRooms);
        Assert.Equal(15, config.ArtifactRetentionDays);
    }

    [Fact]
    public void WorkspaceConfiguration_ShouldSerializeAndDeserializeAiUsagePolicyWithLanguageSpecificRules_Successfully()
    {
        // Arrange
        var originalPolicy = new AiUsagePolicyConfiguration(
            AllowExternalLlm: true,
            RedactPii: new PiiRedactionConfiguration(Enabled: true),
            Dlp: new DlpConfiguration(Enabled: true, KeywordsBlacklist: new List<string> { "bí mật", "nhạy cảm" }),
            TranslationProfile: new TranslationProfileConfiguration(
                TranslationTone: "professional",
                LanguageSpecificRules: new LanguageSpecificRules(
                    VietnameseHonorificStyle: "formal_hierarchical",
                    JapaneseHonorificStyle: "keigo_teineigo"
                )
            )
        );

        var config = new WorkspaceConfiguration
        {
            AiUsagePolicy = originalPolicy
        };

        // Act
        var json = JsonSerializer.Serialize(config);
        var deserializedConfig = JsonSerializer.Deserialize<WorkspaceConfiguration>(json);

        // Assert
        Assert.NotNull(deserializedConfig);
        Assert.NotNull(deserializedConfig.AiUsagePolicy);
        Assert.True(deserializedConfig.AiUsagePolicy.AllowExternalLlm);

        Assert.NotNull(deserializedConfig.AiUsagePolicy.RedactPii);
        Assert.True(deserializedConfig.AiUsagePolicy.RedactPii.Enabled);

        Assert.NotNull(deserializedConfig.AiUsagePolicy.Dlp);
        Assert.True(deserializedConfig.AiUsagePolicy.Dlp.Enabled);
        Assert.NotNull(deserializedConfig.AiUsagePolicy.Dlp.KeywordsBlacklist);
        Assert.Contains("bí mật", deserializedConfig.AiUsagePolicy.Dlp.KeywordsBlacklist);

        Assert.NotNull(deserializedConfig.AiUsagePolicy.TranslationProfile);
        Assert.Equal("professional", deserializedConfig.AiUsagePolicy.TranslationProfile.TranslationTone);

        Assert.NotNull(deserializedConfig.AiUsagePolicy.TranslationProfile.LanguageSpecificRules);
        Assert.Equal("formal_hierarchical", deserializedConfig.AiUsagePolicy.TranslationProfile.LanguageSpecificRules.VietnameseHonorificStyle);
        Assert.Equal("keigo_teineigo", deserializedConfig.AiUsagePolicy.TranslationProfile.LanguageSpecificRules.JapaneseHonorificStyle);
    }

    [Fact]
    public void WorkspaceConfiguration_ShouldNormalizeAllowExternalLlmToTrue_WhenDeserializedFromFalse()
    {
        // Arrange
        var json = "{\"AiUsagePolicy\":{\"AllowExternalLlm\":false}}";

        // Act
        var config = JsonSerializer.Deserialize<WorkspaceConfiguration>(json);

        // Assert
        Assert.NotNull(config);
        Assert.NotNull(config.AiUsagePolicy);
        Assert.True(config.AiUsagePolicy.AllowExternalLlm);
    }

    /// <summary>
    /// WT-646. The plugin-policy defaults exist to make this feature invisible to a workspace that
    /// has not configured it. If any of these three flips, every existing workspace changes
    /// behaviour on the next deploy.
    /// </summary>
    [Fact]
    public void PluginPolicy_ShouldDefaultToTodaysBehaviour_WhenUnconfigured()
    {
        var config = new WorkspaceConfiguration();

        Assert.Null(config.AllowedPluginKeys);        // no allowlist, defer to AllowAnyPlugins
        Assert.True(config.AllowMemberPluginInstall); // members may install, as they do today
        Assert.False(config.RequirePluginApproval);   // no approval gate, as today
    }

    [Fact]
    public void AllowedPluginKeys_ShouldTrimAndDeduplicateCaseInsensitively_WhenSet()
    {
        var config = new WorkspaceConfiguration
        {
            AllowedPluginKeys = new List<string> { "notion", " notion ", "NOTION", "google-calendar" }
        };

        Assert.Equal(new List<string> { "notion", "google-calendar" }, config.AllowedPluginKeys);
    }

    [Fact]
    public void AllowedPluginKeys_ShouldStayNull_WhenSetToNull()
    {
        // The single most important line in this file. A `?? new List<string>()` anywhere on the
        // write path turns "no allowlist configured" into "allowlist permitting nothing", which
        // would silently disable plugins in every workspace that predates WT-646.
        var config = new WorkspaceConfiguration { AllowedPluginKeys = null };

        Assert.Null(config.AllowedPluginKeys);
    }

    [Fact]
    public void AllowedPluginKeys_ShouldStayEmpty_WhenSetToEmptyList()
    {
        // The other side of the same distinction: an empty list is a real, deliberate policy and
        // must not be normalized into null.
        var config = new WorkspaceConfiguration { AllowedPluginKeys = new List<string>() };

        Assert.NotNull(config.AllowedPluginKeys);
        Assert.Empty(config.AllowedPluginKeys);
    }
}
