using NSubstitute;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Application.Services;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.AssistantService.Domain.Entities;
using WarpTalk.AssistantService.Domain.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.AssistantService.Tests.Plugins;

/// <summary>
/// The workspace-scoped read of <c>plugin_tool_audits</c>. WT-646.
/// </summary>
public class PluginToolAuditQueryServiceTests
{
    private static readonly Guid WorkspaceId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid CallerId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid PluginId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IPluginToolAuditRepository _auditRepository = Substitute.For<IPluginToolAuditRepository>();
    private readonly IWorkspaceMembershipClient _membershipClient = Substitute.For<IWorkspaceMembershipClient>();

    public PluginToolAuditQueryServiceTests()
    {
        _unitOfWork.PluginToolAuditRepository.Returns(_auditRepository);
    }

    [Theory]
    [InlineData(WorkspaceRoleConstants.Owner)]
    [InlineData(WorkspaceRoleConstants.Admin)]
    public async Task ReturnsUsage_ForAnOwnerOrAdminOfTheWorkspace(string roleName)
    {
        ConfigureCaller(roleName);
        ConfigureAudits(Audit("google_drive", "google_drive_search", "success"));

        var result = await CreateSut().ListWorkspaceAuditsAsync(
            WorkspaceId, CallerId, pluginKey: null, userId: null, skip: 0, take: 0);

        Assert.True(result.IsSuccess);
        var row = Assert.Single(result.Value!);
        Assert.Equal("google_drive", row.PluginKey);
        Assert.Equal("google_drive_search", row.ToolName);
        Assert.Equal("success", row.ResultStatus);
    }

    [Fact]
    public async Task RefusesAnOrdinaryMemberOfTheWorkspace()
    {
        // Not an outsider - a member of this very workspace. The rows say what every colleague
        // asked a plugin to do, which is a governance view, not a member's own history.
        ConfigureCaller("Member");

        var result = await CreateSut().ListWorkspaceAuditsAsync(
            WorkspaceId, CallerId, pluginKey: null, userId: null, skip: 0, take: 0);

        Assert.False(result.IsSuccess);
        Assert.Equal(PluginConstants.ErrorCodes.PermissionDenied, result.ErrorCode);
        await _auditRepository.DidNotReceive().ListForWorkspaceAsync(
            Arg.Any<Guid>(),
            Arg.Any<string?>(),
            Arg.Any<Guid?>(),
            Arg.Any<int>(),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RefusesAnInactiveOwner()
    {
        _membershipClient.GetMembershipAsync(WorkspaceId, CallerId, Arg.Any<CancellationToken>())
            .Returns(new WorkspaceMembership(IsMember: true, RoleName: WorkspaceRoleConstants.Owner, IsActive: false));

        var result = await CreateSut().ListWorkspaceAuditsAsync(
            WorkspaceId, CallerId, pluginKey: null, userId: null, skip: 0, take: 0);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task RefusesWhenTheWorkspaceServiceCannotAnswer()
    {
        // WorkspaceMembership.None is what the gRPC client returns on an outage, and it has to
        // read as "not an admin" rather than opening a governance view to anyone who asks.
        _membershipClient.GetMembershipAsync(WorkspaceId, CallerId, Arg.Any<CancellationToken>())
            .Returns(WorkspaceMembership.None);

        var result = await CreateSut().ListWorkspaceAuditsAsync(
            WorkspaceId, CallerId, pluginKey: null, userId: null, skip: 0, take: 0);

        Assert.False(result.IsSuccess);
    }

    [Theory]
    [InlineData(0, PluginToolAuditQueryService.DefaultPageSize)]
    [InlineData(-5, PluginToolAuditQueryService.DefaultPageSize)]
    [InlineData(25, 25)]
    [InlineData(10_000, PluginToolAuditQueryService.MaxPageSize)]
    public async Task ClampsThePageSize(int requested, int expected)
    {
        // An audit table has no upper bound, so an unclamped page size is a way to ask this
        // service to materialise all of it.
        ConfigureCaller(WorkspaceRoleConstants.Owner);
        ConfigureAudits();

        await CreateSut().ListWorkspaceAuditsAsync(
            WorkspaceId, CallerId, pluginKey: null, userId: null, skip: -1, take: requested);

        await _auditRepository.Received(1).ListForWorkspaceAsync(
            WorkspaceId,
            null,
            null,
            0,
            expected,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DoesNotExposeTheRecordedToolArguments()
    {
        // input_summary holds the first 500 characters of what a member typed. An Owner needs to
        // know which plugin, which tool, by whom and whether it worked; handing them the argument
        // text is a different decision than a usage log.
        ConfigureCaller(WorkspaceRoleConstants.Owner);
        var audit = Audit("google_drive", "google_drive_search", "success");
        audit.InputSummary = """{"query":"severance package"}""";
        ConfigureAudits(audit);

        var result = await CreateSut().ListWorkspaceAuditsAsync(
            WorkspaceId, CallerId, pluginKey: null, userId: null, skip: 0, take: 0);

        var row = Assert.Single(result.Value!);
        Assert.DoesNotContain(
            "severance",
            string.Join('|', typeof(WarpTalk.AssistantService.Application.DTOs.PluginToolAuditDto)
                .GetProperties()
                .Select(property => property.GetValue(row)?.ToString() ?? string.Empty)),
            StringComparison.OrdinalIgnoreCase);
    }

    private void ConfigureCaller(string roleName) =>
        _membershipClient.GetMembershipAsync(WorkspaceId, CallerId, Arg.Any<CancellationToken>())
            .Returns(new WorkspaceMembership(IsMember: true, RoleName: roleName, IsActive: true));

    private void ConfigureAudits(params PluginToolAudit[] audits) =>
        _auditRepository.ListForWorkspaceAsync(
                Arg.Any<Guid>(),
                Arg.Any<string?>(),
                Arg.Any<Guid?>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(audits);

    private static PluginToolAudit Audit(string pluginKey, string toolName, string resultStatus) =>
        new()
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
            UserId = CallerId,
            PluginId = PluginId,
            PluginKey = pluginKey,
            ToolName = toolName,
            ResultStatus = resultStatus,
            CreatedAt = DateTime.UtcNow,
        };

    private PluginToolAuditQueryService CreateSut() => new(_unitOfWork, _membershipClient);
}
