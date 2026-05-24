using System.Collections.Generic;
using System.Text.Json;
using Xunit;
using WarpTalk.AuthService.Domain.Settings;
using WarpTalk.AuthService.Domain.Constants;

namespace WarpTalk.AuthService.Tests;

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
}
