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
        var json = "{\"DefaultLanguage\":null,\"Timezone\":\"   \",\"AllowedTargetLanguages\":null,\"MaxActiveRooms\":-5,\"ArtifactRetentionDays\":0}";

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
}
