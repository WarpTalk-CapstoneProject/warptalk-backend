using WarpTalk.WorkspaceService.Application.DTOs.Workspace;
using WarpTalk.WorkspaceService.Application.Validators;
using Xunit;

namespace WarpTalk.WorkspaceService.Tests;

public class WorkspaceSettingsValidatorTests
{
    private static WorkspaceSettingsDto Settings(
        int maxActiveRooms = 5,
        int artifactRetentionDays = 30,
        List<string>? verifiedDomains = null,
        bool requireVerifiedDomainForInternal = false) =>
        new(
            "en",
            "UTC",
            new List<string> { "en" },
            true,
            maxActiveRooms,
            artifactRetentionDays,
            true,
            verifiedDomains ?? new List<string>(),
            true,
            requireVerifiedDomainForInternal,
            null,
            false);

    [Theory]
    [InlineData(1, 1)]
    [InlineData(50, 3650)]
    public void AcceptsSupportedNumericBoundaries(int maxActiveRooms, int artifactRetentionDays)
    {
        var result = WorkspaceSettingsValidator.Validate(Settings(maxActiveRooms, artifactRetentionDays));

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData(0, 30, "maxActiveRooms")]
    [InlineData(51, 30, "maxActiveRooms")]
    [InlineData(5, 0, "artifactRetentionDays")]
    [InlineData(5, -1, "artifactRetentionDays")]
    [InlineData(5, 3651, "artifactRetentionDays")]
    public void RejectsOutOfRangeValues(int maxActiveRooms, int artifactRetentionDays, string field)
    {
        var result = WorkspaceSettingsValidator.Validate(Settings(maxActiveRooms, artifactRetentionDays));

        Assert.False(result.IsValid);
        Assert.Contains(field, result.Errors.Keys);
    }

    [Fact]
    public void RejectsEmptyVerifiedDomains_WhenInternalDomainVerificationRequired()
    {
        var result = WorkspaceSettingsValidator.Validate(Settings(requireVerifiedDomainForInternal: true));

        Assert.False(result.IsValid);
        Assert.Contains("verifiedDomains", result.Errors.Keys);
    }

    [Fact]
    public void AcceptsOmittedVerifiedDomains_WhenInternalDomainVerificationDisabled()
    {
        var result = WorkspaceSettingsValidator.Validate(Settings(requireVerifiedDomainForInternal: false));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void AcceptsVerifiedDomains_WhenInternalDomainVerificationRequired()
    {
        var result = WorkspaceSettingsValidator.Validate(Settings(
            verifiedDomains: new List<string> { "company.com" },
            requireVerifiedDomainForInternal: true));

        Assert.True(result.IsValid);
    }
}
