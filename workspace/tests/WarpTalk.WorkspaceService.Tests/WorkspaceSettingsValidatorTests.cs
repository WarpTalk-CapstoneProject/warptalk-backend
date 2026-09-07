using WarpTalk.WorkspaceService.Application.DTOs.Workspace;
using WarpTalk.WorkspaceService.Application.Validators;
using WarpTalk.WorkspaceService.Domain.Constants;
using Xunit;

namespace WarpTalk.WorkspaceService.Tests;

public class WorkspaceSettingsValidatorTests
{
    private static readonly IReadOnlyCollection<string> NoDomains = new List<string>();

    private static WorkspaceSettingsDto Settings(
        int maxActiveRooms = 5,
        int artifactRetentionDays = 30,
        int invitationExpiryDays = 7,
        bool requireVerifiedDomainForInternal = false,
        List<string>? mirroredDomains = null,
        List<string>? allowedPluginKeys = null) =>
        new(
            "en",
            "UTC",
            new List<string> { "vi" },
            true,
            maxActiveRooms,
            artifactRetentionDays,
            mirroredDomains ?? new List<string>(),
            true,
            requireVerifiedDomainForInternal,
            null,
            false,
            invitationExpiryDays)
        { AllowedPluginKeys = allowedPluginKeys };

    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(50, 3650, 365)]
    public void AcceptsSupportedNumericBoundaries(int maxActiveRooms, int artifactRetentionDays, int invitationExpiryDays)
    {
        var result = WorkspaceSettingsValidator.Validate(
            Settings(maxActiveRooms, artifactRetentionDays, invitationExpiryDays),
            NoDomains);

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
        var result = WorkspaceSettingsValidator.Validate(Settings(maxActiveRooms, artifactRetentionDays), NoDomains);

        Assert.False(result.IsValid);
        Assert.Contains(field, result.Errors.Keys);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(366)]
    public void RejectsOutOfRangeInvitationExpiryDays(int invitationExpiryDays)
    {
        var result = WorkspaceSettingsValidator.Validate(Settings(invitationExpiryDays: invitationExpiryDays), NoDomains);

        Assert.False(result.IsValid);
        Assert.Contains("invitationExpiryDays", result.Errors.Keys);
    }

    /// <summary>
    /// The two directions the settings JSON and workspace_verified_domains can disagree. Both
    /// used to be decided by the JSON, and the JSON is written only on a settings save while
    /// domains change through VerifiedDomainService — so both verdicts were wrong.
    /// </summary>
    [Fact]
    public void RequireVerifiedDomain_AcceptsWhenTableHasDomain_EvenIfMirrorIsEmpty()
    {
        // An Owner who just added a domain: the table has it, the JSON has not caught up. This
        // save used to be refused with VerifiedDomainsRequired while the UI showed the domain.
        var result = WorkspaceSettingsValidator.Validate(
            Settings(requireVerifiedDomainForInternal: true, mirroredDomains: new List<string>()),
            new[] { "acme.com" });

        Assert.True(result.IsValid);
    }

    [Fact]
    public void RequireVerifiedDomain_RejectsWhenTableIsEmpty_EvenIfMirrorStillListsDomain()
    {
        // The dangerous direction: the workspace's only domain has been revoked, but the stale
        // JSON still names it. This used to pass, leaving domain policy on with nothing behind it.
        var result = WorkspaceSettingsValidator.Validate(
            Settings(requireVerifiedDomainForInternal: true, mirroredDomains: new List<string> { "acme.com" }),
            Array.Empty<string>());

        Assert.False(result.IsValid);
        Assert.Contains("verifiedDomains", result.Errors.Keys);
    }

    /// <summary>
    /// WT-646. The allowlist's null/empty distinction, from the validator's side.
    /// </summary>
    [Fact]
    public void AllowedPluginKeys_AcceptsNull_MeaningNoAllowlistConfigured()
    {
        // Every workspace that predates WT-646 looks like this. If this ever starts failing, no
        // existing workspace can save its settings at all.
        var result = WorkspaceSettingsValidator.Validate(Settings(allowedPluginKeys: null), NoDomains);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void AllowedPluginKeys_AcceptsEmptyList_AsAnAllowlistPermittingNothing()
    {
        // Strict, but a policy somebody may genuinely want. It is not the same thing as null and
        // must not be refused as if it were a mistake.
        var result = WorkspaceSettingsValidator.Validate(
            Settings(allowedPluginKeys: new List<string>()),
            NoDomains);

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void AllowedPluginKeys_RejectsBlankEntry(string blankKey)
    {
        // Refused rather than silently dropped: a payload with a blank entry is not the payload
        // the caller meant to send, and saving a quietly different allowlist than the one they
        // submitted is the worse of the two outcomes for a security setting.
        var result = WorkspaceSettingsValidator.Validate(
            Settings(allowedPluginKeys: new List<string> { "google-calendar", blankKey }),
            NoDomains);

        Assert.False(result.IsValid);
        Assert.Contains("allowedPluginKeys", result.Errors.Keys);
    }

    [Fact]
    public void AllowedPluginKeys_RejectsListOverTheCap()
    {
        var tooMany = Enumerable.Range(0, WorkspaceConstants.MaxWorkspaceAllowedPluginKeys + 1)
            .Select(index => $"plugin-{index}")
            .ToList();

        var result = WorkspaceSettingsValidator.Validate(Settings(allowedPluginKeys: tooMany), NoDomains);

        Assert.False(result.IsValid);
        Assert.Contains("allowedPluginKeys", result.Errors.Keys);
    }

    [Fact]
    public void AllowedPluginKeys_AcceptsUnknownKey_BecauseTheCatalogIsAnotherServicesTable()
    {
        // Not a gap left by accident. The plugin catalog lives in AssistantService's database and
        // the workspace service has no read path to it — so an unrecognised key is accepted here
        // and simply matches no plugin at enforcement time. Pinning this down in a test so nobody
        // "fixes" it later by hard-coding a key list that goes stale on the next catalog change.
        var result = WorkspaceSettingsValidator.Validate(
            Settings(allowedPluginKeys: new List<string> { "no-such-plugin-anywhere" }),
            NoDomains);

        Assert.True(result.IsValid);
    }
}
