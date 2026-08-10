using WarpTalk.WorkspaceService.Application.DTOs.Workspace;
using WarpTalk.WorkspaceService.Application.Validators;
using Xunit;

namespace WarpTalk.WorkspaceService.Tests;

public class WorkspaceSettingsValidatorTests
{
    private static WorkspaceSettingsDto Settings(
        int maxActiveRooms = 5,
        int artifactRetentionDays = 30,
        int invitationExpiryDays = 7) =>
        new(
            "en",
            "UTC",
            new List<string> { "vi" },
            true,
            maxActiveRooms,
            artifactRetentionDays,
            new List<string>(),
            true,
            false,
            null,
            false,
            invitationExpiryDays);

    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(50, 3650, 365)]
    public void AcceptsSupportedNumericBoundaries(int maxActiveRooms, int artifactRetentionDays, int invitationExpiryDays)
    {
        var result = WorkspaceSettingsValidator.Validate(Settings(maxActiveRooms, artifactRetentionDays, invitationExpiryDays));

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData(0, 30, "maxActiveRooms")]
    [InlineData(51, 30, "maxActiveRooms")]
    [InlineData(5, 0, "artifactRetentionDays")]
    [InlineData(5, -1, "artifactRetentionDays")]
    [InlineData(5, 3651, "artifactRetentionDays")]
    public void RejectsOutOfRangeNumericBoundaries(int maxActiveRooms, int artifactRetentionDays, string field)
    {
        var result = WorkspaceSettingsValidator.Validate(Settings(maxActiveRooms, artifactRetentionDays));

        Assert.False(result.IsValid);
        Assert.Contains(field, result.Errors.Keys);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(366)]
    public void RejectsOutOfRangeInvitationExpiryDays(int invitationExpiryDays)
    {
        var result = WorkspaceSettingsValidator.Validate(Settings(invitationExpiryDays: invitationExpiryDays));

        Assert.False(result.IsValid);
        Assert.Contains("invitationExpiryDays", result.Errors.Keys);
    }
}
