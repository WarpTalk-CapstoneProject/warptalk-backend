using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using NSubstitute;
using WarpTalk.Shared;
using WarpTalk.Shared.Protos;
using WarpTalk.WorkspaceService.API.GrpcServices;
using WarpTalk.WorkspaceService.Application.DTOs;
using WarpTalk.WorkspaceService.Application.Interfaces;
using Xunit;

namespace WarpTalk.WorkspaceService.Tests;

/// <summary>
/// WT-239 left this boundary with request parsing and response mapping only. The
/// membership and workspace-policy rules these tests used to drive through gRPC now
/// live in WorkspaceDirectoryServiceTests.
/// </summary>
public class WorkspaceGrpcServiceTests
{
    private readonly IWorkspaceDirectoryService _workspaceDirectory;
    private readonly IWorkspaceCoMembershipService _coMembership;
    private readonly WorkspaceGrpcService _service;
    private readonly ServerCallContext _context;

    public WorkspaceGrpcServiceTests()
    {
        // WT-263: no IBillingSubscriptionClient here at all — and after WT-239 no unit of work
        // either. The boundary takes the directory service and, since WT-335, the co-membership
        // service that answers whether two users share a tenant; the entitlement snapshot cases
        // that used to be arranged here now live in WorkspaceDirectoryServiceTests against the
        // layer that reads it.
        _workspaceDirectory = Substitute.For<IWorkspaceDirectoryService>();
        _coMembership = Substitute.For<IWorkspaceCoMembershipService>();
        _service = new WorkspaceGrpcService(_workspaceDirectory, _coMembership);
        _context = new TestServerCallContext(CancellationToken.None);
    }

    [Fact]
    public async Task ListUserWorkspaceAudience_SendsAnEmptyPlanForAWorkspaceWithoutOne_AndNothingForABadId()
    {
        var userId = Guid.NewGuid();
        var onPro = Guid.NewGuid();
        var noPlan = Guid.NewGuid();
        _workspaceDirectory
            .ListUserWorkspaceAudienceAsync(userId, Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<UserWorkspaceAudienceDto>>(new List<UserWorkspaceAudienceDto>
            {
                new(onPro, "pro"),
                new(noPlan, null),
            }));

        var response = await _service.ListUserWorkspaceAudience(
            new ListUserWorkspaceAudienceRequest { UserId = userId.ToString() }, _context);
        var bad = await _service.ListUserWorkspaceAudience(
            new ListUserWorkspaceAudienceRequest { UserId = "not-a-guid" }, _context);

        Assert.Equal(
            new[] { (onPro.ToString(), "pro"), (noPlan.ToString(), string.Empty) },
            response.Workspaces.Select(item => (item.WorkspaceId, item.PlanSlug)));
        Assert.Empty(bad.Workspaces);
    }

    [Fact]
    public async Task ListPlatformWorkspaces_CarriesPlanOwnerAndPluginSwitch_WithEmptyForNoPlan()
    {
        var owner = Guid.NewGuid();
        var biz = Guid.NewGuid();
        var trial = Guid.NewGuid();
        _workspaceDirectory
            .ListPlatformWorkspacesAsync(Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<PlatformWorkspaceDto>>(new List<PlatformWorkspaceDto>
            {
                new(biz, "Biz Co", "biz-co", "active", owner, "business", 12, true),
                new(trial, "Trial", "trial", "suspended", owner, null, 1, false),
            }));

        var response = await _service.ListPlatformWorkspaces(new ListPlatformWorkspacesRequest(), _context);

        Assert.Equal(2, response.Workspaces.Count);
        var first = response.Workspaces[0];
        Assert.Equal((biz.ToString(), "Biz Co", "business", 12, true, owner.ToString()),
            (first.WorkspaceId, first.Name, first.PlanSlug, first.MemberCount, first.AllowAnyPlugins, first.OwnerUserId));
        Assert.Equal((string.Empty, "suspended", false),
            (response.Workspaces[1].PlanSlug, response.Workspaces[1].Status, response.Workspaces[1].AllowAnyPlugins));
    }

    [Fact]
    public async Task ListActiveWorkspaceMemberships_DropsBadIds_AndSkipsTheCallWhenNoneAreLeft()
    {
        var user = Guid.NewGuid();
        var workspace = Guid.NewGuid();
        _workspaceDirectory
            .ListActiveMembershipsForUsersAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<(Guid UserId, Guid WorkspaceId)>>(new List<(Guid, Guid)> { (user, workspace) }));

        var request = new ListActiveWorkspaceMembershipsRequest();
        request.UserIds.AddRange([user.ToString(), "not-a-guid"]);
        var response = await _service.ListActiveWorkspaceMemberships(request, _context);
        var empty = new ListActiveWorkspaceMembershipsRequest();
        empty.UserIds.Add("nope");
        var none = await _service.ListActiveWorkspaceMemberships(empty, _context);

        var pair = Assert.Single(response.Memberships);
        Assert.Equal((user.ToString(), workspace.ToString()), (pair.UserId, pair.WorkspaceId));
        Assert.Empty(none.Memberships);
        await _workspaceDirectory.Received(1).ListActiveMembershipsForUsersAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 1 && ids.Contains(user)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetWorkspaceMemberDetails_MapsMemberOntoResponse()
    {
        _workspaceDirectory
            .GetMemberDetailsAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<WorkspaceMemberDetailsDto?>(
                new WorkspaceMemberDetailsDto("Admin", "internal", true, true)));

        var response = await _service.GetWorkspaceMemberDetails(
            new GetWorkspaceMemberRequest
            {
                WorkspaceId = Guid.NewGuid().ToString(),
                UserId = Guid.NewGuid().ToString()
            },
            _context);

        Assert.True(response.IsMember);
        Assert.Equal("Admin", response.RoleName);
        Assert.Equal("internal", response.MembershipType);
        Assert.True(response.IsActive);
        Assert.True(response.CanCreateMeetings);
    }

    [Fact]
    public async Task GetWorkspaceMemberDetails_ReturnsIsMemberFalse_WhenNotAMember()
    {
        _workspaceDirectory
            .GetMemberDetailsAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<WorkspaceMemberDetailsDto?>(null));

        var response = await _service.GetWorkspaceMemberDetails(
            new GetWorkspaceMemberRequest
            {
                WorkspaceId = Guid.NewGuid().ToString(),
                UserId = Guid.NewGuid().ToString()
            },
            _context);

        Assert.False(response.IsMember);
    }

    [Fact]
    public async Task GetWorkspaceMemberDetails_ReturnsIsMemberFalse_WhenIdsUnparseable()
    {
        var response = await _service.GetWorkspaceMemberDetails(
            new GetWorkspaceMemberRequest { WorkspaceId = "not-a-guid", UserId = "also-not" },
            _context);

        Assert.False(response.IsMember);
        await _workspaceDirectory.DidNotReceiveWithAnyArgs()
            .GetMemberDetailsAsync(default, default, default);
    }

    [Fact]
    public async Task GetWorkspaceNames_MapsNamesOntoResponse()
    {
        var firstId = Guid.NewGuid();
        _workspaceDirectory
            .GetWorkspaceNamesAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<WorkspaceNameDto>>(
                new[] { new WorkspaceNameDto(firstId, "WarpTalk Team") }));

        var request = new GetWorkspaceNamesRequest();
        request.WorkspaceIds.Add(firstId.ToString());
        request.WorkspaceIds.Add(Guid.NewGuid().ToString());

        var response = await _service.GetWorkspaceNames(request, _context);

        Assert.Single(response.Workspaces);
        Assert.Equal(firstId.ToString(), response.Workspaces[0].WorkspaceId);
        Assert.Equal("WarpTalk Team", response.Workspaces[0].WorkspaceName);
    }

    [Fact]
    public async Task ValidateMeetingCreation_MapsDecisionOntoResponse()
    {
        _workspaceDirectory
            .ValidateMeetingCreationAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(MeetingCreationDecisionDto.Denied("User does not have permission to create meetings.")));

        var response = await _service.ValidateMeetingCreation(
            new ValidateMeetingCreationRequest
            {
                WorkspaceId = Guid.NewGuid().ToString(),
                UserId = Guid.NewGuid().ToString()
            },
            _context);

        Assert.False(response.IsAllowed);
        Assert.Contains("permission", response.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateMeetingCreation_FailsClosed_WhenDecisionUnavailable()
    {
        _workspaceDirectory
            .ValidateMeetingCreationAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<MeetingCreationDecisionDto>("Workspace lookup failed."));

        var response = await _service.ValidateMeetingCreation(
            new ValidateMeetingCreationRequest
            {
                WorkspaceId = Guid.NewGuid().ToString(),
                UserId = Guid.NewGuid().ToString()
            },
            _context);

        Assert.False(response.IsAllowed);
    }

    [Fact]
    public async Task ValidateMeetingCreation_Denies_WhenIdsUnparseable()
    {
        var response = await _service.ValidateMeetingCreation(
            new ValidateMeetingCreationRequest { WorkspaceId = "nope", UserId = "nope" },
            _context);

        Assert.False(response.IsAllowed);
        Assert.Contains("Invalid", response.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetWorkspaceSettings_MapsSettingsOntoResponse()
    {
        _workspaceDirectory
            .GetSettingsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new WorkspaceSettingsSnapshotDto(15, true, false, true, true, new[] { "vi", "en" })));

        var response = await _service.GetWorkspaceSettings(
            new GetWorkspaceSettingsRequest { WorkspaceId = Guid.NewGuid().ToString() },
            _context);

        Assert.Equal(15, response.ArtifactRetentionDays);
        Assert.True(response.AllowExternalCollaboration);
        Assert.True(response.AllowExternalLlm);
        Assert.True(response.UseGlobalGlossary);
    }

    /// <summary>
    /// The whole of a workspace's plugin policy on the wire is this one flag. It is what the
    /// assistant's guard reads, so it has to survive the mapping in both directions.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetWorkspaceSettings_CarriesAllowAnyPlugins(bool allowAnyPlugins)
    {
        _workspaceDirectory
            .GetSettingsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new WorkspaceSettingsSnapshotDto(
                15, true, false, true, true, new[] { "vi" },
                AllowAnyPlugins: allowAnyPlugins)));

        var response = await _service.GetWorkspaceSettings(
            new GetWorkspaceSettingsRequest { WorkspaceId = Guid.NewGuid().ToString() },
            _context);

        Assert.Equal(allowAnyPlugins, response.AllowAnyPlugins);
    }

    /// <summary>
    /// The assistant's plugin plan rule reads this. Empty means no plan, which no rule matches.
    /// </summary>
    [Theory]
    [InlineData("business", "business")]
    [InlineData(null, "")]
    public async Task GetWorkspaceSettings_CarriesThePlanSlug(string? plan, string onTheWire)
    {
        _workspaceDirectory
            .GetSettingsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new WorkspaceSettingsSnapshotDto(
                15, true, false, true, true, new[] { "vi" }, PlanSlug: plan)));

        var response = await _service.GetWorkspaceSettings(
            new GetWorkspaceSettingsRequest { WorkspaceId = Guid.NewGuid().ToString() },
            _context);

        Assert.Equal(onTheWire, response.PlanSlug);
    }

    /// <summary>WT-707: the plan's language limit travels on the wire; 0 means no quota.</summary>
    [Theory]
    [InlineData(2, 2)]
    [InlineData(null, 0)]
    public async Task GetWorkspaceSettings_CarriesMaxLanguages(int? maxLanguages, int expected)
    {
        _workspaceDirectory
            .GetSettingsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new WorkspaceSettingsSnapshotDto(
                15, true, false, true, true, new[] { "vi" },
                MaxLanguages: maxLanguages)));

        var response = await _service.GetWorkspaceSettings(
            new GetWorkspaceSettingsRequest { WorkspaceId = Guid.NewGuid().ToString() },
            _context);

        Assert.Equal(expected, response.MaxLanguages);
    }

    [Fact]
    public async Task GetWorkspaceSettings_ThrowsNotFound_WhenLookupFails()
    {
        _workspaceDirectory
            .GetSettingsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<WorkspaceSettingsSnapshotDto>("Workspace not found.", ErrorCodes.NotFound));

        var exception = await Assert.ThrowsAsync<RpcException>(() => _service.GetWorkspaceSettings(
            new GetWorkspaceSettingsRequest { WorkspaceId = Guid.NewGuid().ToString() },
            _context));

        Assert.Equal(StatusCode.NotFound, exception.StatusCode);
    }

    [Fact]
    public async Task GetWorkspaceSettings_ThrowsInvalidArgument_WhenWorkspaceIdUnparseable()
    {
        var exception = await Assert.ThrowsAsync<RpcException>(() => _service.GetWorkspaceSettings(
            new GetWorkspaceSettingsRequest { WorkspaceId = "not-a-guid" },
            _context));

        Assert.Equal(StatusCode.InvalidArgument, exception.StatusCode);
    }

    [Fact]
    public async Task GetWorkspacePreflightDetails_MapsPreflightOntoResponse()
    {
        _workspaceDirectory
            .GetPreflightAsync(Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new WorkspacePreflightDto(true, "WarpTalk Team", "warptalk", true, false)));

        var response = await _service.GetWorkspacePreflightDetails(
            new GetWorkspacePreflightRequest
            {
                WorkspaceId = Guid.NewGuid().ToString(),
                UserEmail = "someone@example.com"
            },
            _context);

        Assert.True(response.IsActive);
        Assert.Equal("WarpTalk Team", response.WorkspaceName);
        Assert.Equal("warptalk", response.WorkspaceSlug);
        Assert.True(response.IsDomainMatched);
        Assert.False(response.AllowExternalCollaboration);
        Assert.Equal(string.Empty, response.OwnerUserId);
    }

    [Fact]
    public async Task GetWorkspacePreflightDetails_CarriesTheOwner()
    {
        // AssistantService notifies this user when a member asks for a plugin.
        var ownerId = Guid.NewGuid();
        _workspaceDirectory
            .GetPreflightAsync(Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new WorkspacePreflightDto(true, "WarpTalk Team", "warptalk", false, false, ownerId)));

        var response = await _service.GetWorkspacePreflightDetails(
            new GetWorkspacePreflightRequest { WorkspaceId = Guid.NewGuid().ToString() },
            _context);

        Assert.Equal(ownerId.ToString(), response.OwnerUserId);
    }

    private class TestServerCallContext : ServerCallContext
    {
        private readonly CancellationToken _cancellationToken;

        public TestServerCallContext(CancellationToken cancellationToken)
        {
            _cancellationToken = cancellationToken;
        }

        protected override string MethodCore => "TestMethod";
        protected override string HostCore => "localhost";
        protected override string PeerCore => "127.0.0.1";
        protected override DateTime DeadlineCore => DateTime.MaxValue;
        protected override Metadata RequestHeadersCore => new Metadata();
        protected override CancellationToken CancellationTokenCore => _cancellationToken;
        protected override Metadata ResponseTrailersCore => new Metadata();
        protected override Status StatusCore { get; set; }
        protected override WriteOptions? WriteOptionsCore { get; set; }
        protected override AuthContext AuthContextCore => null!;

        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) => null!;
        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
    }
}
