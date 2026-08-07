using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.Gateway.Services;
using WarpTalk.Shared.Protos;
using Xunit;

namespace WarpTalk.Gateway.Tests;

/// <summary>
/// The predicate behind TranslationRoomHub's host-only methods. It must agree with the REST side
/// (TranslationRoomParticipantService: room host OR workspace Owner/Admin) — host-only would 403
/// the workspace Owners and Admins the web client shows these controls to, which is the WT-188 bug
/// — and it must fail closed when the room cannot be resolved, since an unverifiable caller is
/// exactly what the KNOWN GAP this replaces used to wave through.
/// </summary>
public class RoomHostAuthorityTests
{
    private static readonly Guid RoomId = Guid.NewGuid();
    private static readonly Guid WorkspaceId = Guid.NewGuid();

    [Fact]
    public async Task ReturnsTrue_ForTheRoomHost_WithoutAskingWorkspaceService()
    {
        var hostId = Guid.NewGuid();
        var workspace = new Mock<WorkspaceService.WorkspaceServiceClient>();

        var sut = Create(RoomResponse(hostId), workspace);

        Assert.True(await sut.HasHostAuthorityAsync(RoomId, hostId.ToString()));

        // Host identity is checked first on purpose: the host path must not depend on
        // WorkspaceService being reachable.
        workspace.Verify(
            c => c.GetWorkspaceMemberDetailsAsync(
                It.IsAny<GetWorkspaceMemberRequest>(), null, null, It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData("Owner")]
    [InlineData("Admin")]
    [InlineData("admin")]
    public async Task ReturnsTrue_ForAnActiveWorkspaceOwnerOrAdmin(string roleName)
    {
        var caller = Guid.NewGuid();
        var workspace = WorkspaceClient(new GetWorkspaceMemberResponse
        {
            IsMember = true,
            IsActive = true,
            RoleName = roleName
        });

        var sut = Create(RoomResponse(Guid.NewGuid()), workspace);

        Assert.True(await sut.HasHostAuthorityAsync(RoomId, caller.ToString()));
    }

    [Fact]
    public async Task ReturnsFalse_ForAPlainMember()
    {
        var workspace = WorkspaceClient(new GetWorkspaceMemberResponse
        {
            IsMember = true,
            IsActive = true,
            RoleName = "Member"
        });

        var sut = Create(RoomResponse(Guid.NewGuid()), workspace);

        Assert.False(await sut.HasHostAuthorityAsync(RoomId, Guid.NewGuid().ToString()));
    }

    [Fact]
    public async Task ReturnsFalse_ForAnInactiveOwner()
    {
        var workspace = WorkspaceClient(new GetWorkspaceMemberResponse
        {
            IsMember = true,
            IsActive = false,
            RoleName = "Owner"
        });

        var sut = Create(RoomResponse(Guid.NewGuid()), workspace);

        Assert.False(await sut.HasHostAuthorityAsync(RoomId, Guid.NewGuid().ToString()));
    }

    /// <summary>
    /// Fails CLOSED, unlike the workspace lookup: with no room there is no host to compare against,
    /// so allowing the action would be the unverified trust this type exists to remove.
    /// </summary>
    [Fact]
    public async Task ReturnsFalse_WhenTheRoomLookupFails()
    {
        var room = new Mock<Shared.Protos.TranslationRoomService.TranslationRoomServiceClient>();
        room.Setup(c => c.GetTranslationRoomByIdAsync(
                It.IsAny<GetTranslationRoomRequest>(), null, null, It.IsAny<CancellationToken>()))
            .Throws(new RpcException(new Status(StatusCode.Unavailable, "down")));

        var sut = new RoomHostAuthority(
            room.Object,
            new Mock<WorkspaceService.WorkspaceServiceClient>().Object,
            new NullLogger<RoomHostAuthority>());

        Assert.False(await sut.HasHostAuthorityAsync(RoomId, Guid.NewGuid().ToString()));
    }

    /// <summary>
    /// A WorkspaceService outage may only fail to WIDEN a decision already denied on host identity;
    /// it must not become an error for a legitimate non-host caller. Same reasoning as
    /// WorkspaceMemberGrpcDirectory on the REST side.
    /// </summary>
    [Fact]
    public async Task ReturnsFalse_WhenWorkspaceServiceFails()
    {
        var workspace = new Mock<WorkspaceService.WorkspaceServiceClient>();
        workspace.Setup(c => c.GetWorkspaceMemberDetailsAsync(
                It.IsAny<GetWorkspaceMemberRequest>(), null, null, It.IsAny<CancellationToken>()))
            .Throws(new RpcException(new Status(StatusCode.Unavailable, "down")));

        var sut = Create(RoomResponse(Guid.NewGuid()), workspace);

        Assert.False(await sut.HasHostAuthorityAsync(RoomId, Guid.NewGuid().ToString()));
    }

    [Fact]
    public async Task ReturnsFalse_ForAnEmptyCallerId()
    {
        var sut = Create(RoomResponse(Guid.NewGuid()), new Mock<WorkspaceService.WorkspaceServiceClient>());

        Assert.False(await sut.HasHostAuthorityAsync(RoomId, string.Empty));
    }

    private static GetTranslationRoomResponse RoomResponse(Guid hostId) => new()
    {
        Id = RoomId.ToString(),
        HostId = hostId.ToString(),
        WorkspaceId = WorkspaceId.ToString(),
        Title = "Room",
        Status = "IN_PROGRESS"
    };

    private static RoomHostAuthority Create(
        GetTranslationRoomResponse room,
        Mock<WorkspaceService.WorkspaceServiceClient> workspace)
    {
        var roomClient = new Mock<Shared.Protos.TranslationRoomService.TranslationRoomServiceClient>();
        roomClient.Setup(c => c.GetTranslationRoomByIdAsync(
                It.IsAny<GetTranslationRoomRequest>(), null, null, It.IsAny<CancellationToken>()))
            .Returns(AsyncUnary(room));

        return new RoomHostAuthority(roomClient.Object, workspace.Object, new NullLogger<RoomHostAuthority>());
    }

    private static Mock<WorkspaceService.WorkspaceServiceClient> WorkspaceClient(GetWorkspaceMemberResponse response)
    {
        var mock = new Mock<WorkspaceService.WorkspaceServiceClient>();
        mock.Setup(c => c.GetWorkspaceMemberDetailsAsync(
                It.IsAny<GetWorkspaceMemberRequest>(), null, null, It.IsAny<CancellationToken>()))
            .Returns(AsyncUnary(response));
        return mock;
    }

    private static AsyncUnaryCall<T> AsyncUnary<T>(T value) => new(
        Task.FromResult(value),
        Task.FromResult(new Metadata()),
        () => Status.DefaultSuccess,
        () => new Metadata(),
        () => { });
}
