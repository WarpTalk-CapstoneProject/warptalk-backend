using System.Linq.Expressions;
using System.Text.Json;
using NSubstitute;
using WarpTalk.AssistantService.API.Controllers;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Application.Services;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.AssistantService.Domain.Entities;
using WarpTalk.AssistantService.Domain.Interfaces;

namespace WarpTalk.AssistantService.Tests.Plugins;

/// <summary>
/// The Manage dialog's "who connected this": Owner or Admin only, this workspace's members only,
/// connection metadata only.
/// </summary>
public class WorkspacePluginMemberServiceTests
{
    private static readonly Guid WorkspaceId = Guid.Parse("cccccccc-0000-0000-0000-000000000001");
    private static readonly Guid OwnerId = Guid.Parse("dddddddd-0000-0000-0000-000000000001");
    private static readonly Guid AdminId = Guid.Parse("dddddddd-0000-0000-0000-000000000002");
    private static readonly Guid MemberId = Guid.Parse("dddddddd-0000-0000-0000-000000000003");
    private static readonly Guid OutsiderId = Guid.Parse("dddddddd-0000-0000-0000-000000000004");

    private const string AccessToken = "ya29.secret-access-token";
    private const string RefreshToken = "1//secret-refresh-token";

    private readonly List<Plugin> _plugins = [];
    private readonly List<PluginInstallation> _installations = [];
    private readonly List<PluginConnection> _connections = [];
    private readonly Dictionary<Guid, PluginUsageByUser> _usage = [];
    private IReadOnlyList<Guid>? _memberIds;

    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IWorkspaceMembershipClient _membershipClient = Substitute.For<IWorkspaceMembershipClient>();
    private readonly IWorkspaceDirectoryClient _directoryClient = Substitute.For<IWorkspaceDirectoryClient>();
    private readonly Plugin _notion = WorkspacePluginGuardTests.Marketplace("notion");

    public WorkspacePluginMemberServiceTests()
    {
        var plugins = Substitute.For<IPluginRepository>();
        plugins.FirstOrDefaultAsync(Arg.Any<Expression<Func<Plugin, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => _plugins.FirstOrDefault(call.Arg<Expression<Func<Plugin, bool>>>().Compile()));
        var installations = Substitute.For<IPluginInstallationRepository>();
        installations.FindAsync(Arg.Any<Expression<Func<PluginInstallation, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlyList<PluginInstallation>)_installations
                .Where(call.Arg<Expression<Func<PluginInstallation, bool>>>().Compile()).ToList());
        var connections = Substitute.For<IPluginConnectionRepository>();
        connections.FindAsync(Arg.Any<Expression<Func<PluginConnection, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlyList<PluginConnection>)_connections
                .Where(call.Arg<Expression<Func<PluginConnection, bool>>>().Compile()).ToList());
        var audits = Substitute.For<IPluginToolAuditRepository>();
        audits.GetUsageByUserAsync(WorkspaceId, Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(_ => (IReadOnlyDictionary<Guid, PluginUsageByUser>)_usage);

        _unitOfWork.PluginRepository.Returns(plugins);
        _unitOfWork.PluginInstallationRepository.Returns(installations);
        _unitOfWork.PluginConnectionRepository.Returns(connections);
        _unitOfWork.PluginToolAuditRepository.Returns(audits);

        _membershipClient.GetMembershipAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                if (call.ArgAt<Guid>(0) != WorkspaceId) return WorkspaceMembership.None;
                var user = call.ArgAt<Guid>(1);
                if (user == OwnerId) return new WorkspaceMembership(true, "Owner", true);
                if (user == AdminId) return new WorkspaceMembership(true, "Admin", true);
                if (user == MemberId) return new WorkspaceMembership(true, "Member", true);
                return WorkspaceMembership.None;
            });
        _memberIds = [OwnerId, AdminId, MemberId];
        _directoryClient.ListActiveMemberUserIdsAsync(WorkspaceId, Arg.Any<CancellationToken>())
            .Returns(_ => _memberIds);

        _plugins.Add(_notion);
    }

    private WorkspacePluginMemberService Sut() => new(_unitOfWork, _membershipClient, _directoryClient);

    [Fact]
    public async Task TheOwnerSeesWhoConnected_WhenAndWhenTheyLastUsedIt_MostRecentFirst()
    {
        var connectedAt = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
        var lastUsed = new DateTime(2026, 9, 20, 9, 30, 0, DateTimeKind.Utc);
        Connected(OwnerId, connectedAt);
        Connected(MemberId, connectedAt.AddDays(2));
        _usage[MemberId] = new PluginUsageByUser(lastUsed, 7);

        var rows = (await Sut().ListConnectedMembersAsync(WorkspaceId, OwnerId, "notion")).Value!;

        Assert.Equal([MemberId, OwnerId], rows.Select(r => r.UserId));
        Assert.Equal(lastUsed, rows[0].LastUsedAt);
        Assert.Equal(7, rows[0].ToolCallCount);
        Assert.Equal(connectedAt, rows[1].ConnectedAt);
        Assert.Null(rows[1].LastUsedAt);
        Assert.All(rows, r => Assert.Equal(PluginConstants.ConnectionStatus.Connected, r.ConnectionStatus));
    }

    [Fact]
    public async Task AnAdminMayLook_AMemberMayNot()
    {
        Connected(MemberId, DateTime.UtcNow);

        Assert.True((await Sut().ListConnectedMembersAsync(WorkspaceId, AdminId, "notion")).IsSuccess);

        var asMember = await Sut().ListConnectedMembersAsync(WorkspaceId, MemberId, "notion");
        Assert.Equal(PluginConstants.ErrorCodes.PermissionDenied, asMember.ErrorCode);
        Assert.Equal(403, WorkspacePluginsController.StatusFor(asMember.ErrorCode));
    }

    [Fact]
    public async Task SomeoneOutsideTheWorkspace_WhoConnectedThePlugin_IsNotListed()
    {
        // Installations are personal. Without the member intersection this listed every user on the
        // platform who connected Notion, to the Owner of any workspace that has it.
        Connected(OutsiderId, DateTime.UtcNow);
        Connected(MemberId, DateTime.UtcNow);

        var rows = (await Sut().ListConnectedMembersAsync(WorkspaceId, OwnerId, "notion")).Value!;

        Assert.Equal([MemberId], rows.Select(r => r.UserId));
    }

    [Fact]
    public async Task RemovedOrNeverConnectedInstallations_AreNotConnections()
    {
        Connected(MemberId, DateTime.UtcNow);
        _installations.Single().Status = PluginConstants.InstallationStatus.Disabled;
        _installations.Add(new PluginInstallation
        {
            Id = Guid.NewGuid(), UserId = AdminId, PluginId = _notion.Id,
            Status = PluginConstants.InstallationStatus.Installed, InstalledAt = DateTime.UtcNow, ConnectedAt = null,
        });

        var rows = (await Sut().ListConnectedMembersAsync(WorkspaceId, OwnerId, "notion")).Value!;

        Assert.Empty(rows);
    }

    [Fact]
    public async Task AnExpiredGrant_IsListedAsExpired()
    {
        Connected(MemberId, DateTime.UtcNow);
        _connections.Single().Status = PluginConstants.ConnectionStatus.Expired;

        var row = Assert.Single((await Sut().ListConnectedMembersAsync(WorkspaceId, OwnerId, "notion")).Value!);

        Assert.Equal(PluginConstants.ConnectionStatus.Expired, row.ConnectionStatus);
    }

    [Fact]
    public async Task TheAnswerCarriesNoTokenNorTheProviderAccount()
    {
        Connected(MemberId, DateTime.UtcNow);

        var rows = (await Sut().ListConnectedMembersAsync(WorkspaceId, OwnerId, "notion")).Value!;
        var json = JsonSerializer.Serialize(rows);

        Assert.DoesNotContain(AccessToken, json, StringComparison.Ordinal);
        Assert.DoesNotContain(RefreshToken, json, StringComparison.Ordinal);
        Assert.DoesNotContain("member@notion.example", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Token", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnotherWorkspacesPrivatePlugin_IsUnknownHere()
    {
        _plugins.Add(WorkspacePluginGuardTests.Private("ws_crm_1a2b3c4d", Guid.NewGuid()));

        var result = await Sut().ListConnectedMembersAsync(WorkspaceId, OwnerId, "ws_crm_1a2b3c4d");

        Assert.Equal(PluginConstants.ErrorCodes.UnknownPlugin, result.ErrorCode);
        Assert.Equal(404, WorkspacePluginsController.StatusFor(result.ErrorCode));
    }

    [Fact]
    public async Task WhenTheMemberListCannotBeRead_ItIsA503_NotAnEmptyList()
    {
        Connected(MemberId, DateTime.UtcNow);
        _memberIds = null;

        var result = await Sut().ListConnectedMembersAsync(WorkspaceId, OwnerId, "notion");

        Assert.Equal(WorkspacePluginConstants.ErrorCodes.MembersUnavailable, result.ErrorCode);
        Assert.Equal(503, WorkspacePluginsController.StatusFor(result.ErrorCode));
    }

    private void Connected(Guid userId, DateTime connectedAt)
    {
        _installations.Add(new PluginInstallation
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            PluginId = _notion.Id,
            Status = PluginConstants.InstallationStatus.Installed,
            InstalledAt = connectedAt,
            ConnectedAt = connectedAt,
        });
        _connections.Add(new PluginConnection
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            PluginId = _notion.Id,
            Provider = _notion.Provider,
            ProviderEmail = "member@notion.example",
            Status = PluginConstants.ConnectionStatus.Connected,
            EncryptedAccessToken = AccessToken,
            EncryptedRefreshToken = RefreshToken,
            CreatedAt = connectedAt,
            UpdatedAt = connectedAt,
        });
    }
}
