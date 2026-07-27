using WarpTalk.WorkspaceService.Application.Helpers;
using WarpTalk.WorkspaceService.Domain.Enums;
using Xunit;

namespace WarpTalk.WorkspaceService.Tests;

public class WorkspaceHelperTests
{
    [Theory]
    [InlineData("alice@enterprise.vn", MembershipType.Internal)]
    [InlineData("alice@gmail.com", MembershipType.External)]
    [InlineData(null, MembershipType.External)]
    public void ResolveMembershipType_UsesVerifiedDomainAsSourceOfTruth(
        string? email,
        MembershipType expected)
    {
        var result = WorkspaceHelper.ResolveMembershipType(
            email,
            new[] { "@enterprise.vn" },
            requireVerifiedDomain: true,
            allowSubdomains: false);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void ResolveMembershipType_AllowsSubdomainOnlyWhenConfigured()
    {
        var denied = WorkspaceHelper.ResolveMembershipType(
            "alice@engineering.enterprise.vn",
            new[] { "enterprise.vn" },
            requireVerifiedDomain: true,
            allowSubdomains: false);
        var allowed = WorkspaceHelper.ResolveMembershipType(
            "alice@engineering.enterprise.vn",
            new[] { "enterprise.vn" },
            requireVerifiedDomain: true,
            allowSubdomains: true);

        Assert.Equal(MembershipType.External, denied);
        Assert.Equal(MembershipType.Internal, allowed);
    }

    [Fact]
    public void ResolveMembershipType_DefaultsToInternal_WhenDomainVerificationIsNotRequired()
    {
        var result = WorkspaceHelper.ResolveMembershipType(
            "alice@gmail.com",
            new[] { "enterprise.vn" },
            requireVerifiedDomain: false,
            allowSubdomains: false);

        Assert.Equal(MembershipType.Internal, result);
    }
}
