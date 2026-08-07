using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WarpTalk.Shared;
using WarpTalk.Shared.Protos;
using WarpTalk.TranslationRoomService.Infrastructure.Clients;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Infrastructure;

/// <summary>
/// The one place the workspace's <c>is_active</c> flag turns into a decision this service acts on.
///
/// WorkspaceService has always exposed GetWorkspacePreflightDetails, and it has always reported the
/// tenant's own lifecycle correctly — it simply had no caller anywhere in the repository, which is
/// why suspending a workspace stopped document upload and new invitations while meetings, and the
/// billable STT/TTS they drive, carried on. These pin the wiring that finally consumes it.
/// </summary>
public class WorkspaceMeetingPolicyGrpcClientTests
{
    private static readonly Guid WorkspaceId = Guid.NewGuid();

    [Fact]
    public async Task Denies_WhenTheWorkspaceReportsItselfInactive()
    {
        var sut = Create(Preflight(isActive: false));

        var result = await sut.EnsureWorkspaceCanHostMeetingsAsync(WorkspaceId);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Forbidden, result.ErrorCode);
        Assert.Contains("suspended", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Allows_WhenTheWorkspaceIsLive()
    {
        var sut = Create(Preflight(isActive: true));

        var result = await sut.EnsureWorkspaceCanHostMeetingsAsync(WorkspaceId);

        Assert.True(result.IsSuccess);
    }

    /// <summary>
    /// The email is left empty deliberately. It only drives the verified-domain lookup on the
    /// workspace side, which this caller has no use for — and this check now runs on every room
    /// join in the product, so paying for that query would be a real cost.
    /// </summary>
    [Fact]
    public async Task AsksWithoutAnEmail_SoTheVerifiedDomainLookupIsSkipped()
    {
        var client = new Mock<WorkspaceService.WorkspaceServiceClient>();
        client.Setup(c => c.GetWorkspacePreflightDetailsAsync(
                It.IsAny<GetWorkspacePreflightRequest>(), null, null, It.IsAny<CancellationToken>()))
            .Returns(AsyncUnary(Preflight(isActive: true)));

        await new WorkspaceMeetingPolicyGrpcClient(
                client.Object, NullLogger<WorkspaceMeetingPolicyGrpcClient>.Instance)
            .EnsureWorkspaceCanHostMeetingsAsync(WorkspaceId);

        client.Verify(c => c.GetWorkspacePreflightDetailsAsync(
                It.Is<GetWorkspacePreflightRequest>(r =>
                    r.WorkspaceId == WorkspaceId.ToString() && r.UserEmail == string.Empty),
                null, null, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// FAILS OPEN, and deliberately so — the opposite of ValidateMeetingCreationAsync on the same
    /// client, which fails closed because it IS the permission gate (WT-249).
    ///
    /// This check runs on room join and room start, neither of which depended on WorkspaceService
    /// before it existed. Turning a WorkspaceService outage into "nobody in the product can enter a
    /// meeting" is a far worse outcome than letting an already-suspended tenant finish the call it
    /// is in: that bypass is bounded by the outage and self-corrects the moment service returns,
    /// and the tenant still cannot create anything new.
    /// </summary>
    [Fact]
    public async Task Allows_WhenWorkspaceServiceIsUnreachable()
    {
        var client = new Mock<WorkspaceService.WorkspaceServiceClient>();
        client.Setup(c => c.GetWorkspacePreflightDetailsAsync(
                It.IsAny<GetWorkspacePreflightRequest>(), null, null, It.IsAny<CancellationToken>()))
            .Throws(new RpcException(new Status(StatusCode.Unavailable, "down")));

        var sut = new WorkspaceMeetingPolicyGrpcClient(
            client.Object, NullLogger<WorkspaceMeetingPolicyGrpcClient>.Instance);

        var result = await sut.EnsureWorkspaceCanHostMeetingsAsync(WorkspaceId);

        Assert.True(result.IsSuccess);
    }

    /// <summary>
    /// The contrast that makes the case above a decision rather than an oversight: the creation
    /// gate on this same client turns the same outage into a denial.
    /// </summary>
    [Fact]
    public async Task Creation_StillFailsClosed_WhenWorkspaceServiceIsUnreachable()
    {
        var client = new Mock<WorkspaceService.WorkspaceServiceClient>();
        client.Setup(c => c.ValidateMeetingCreationAsync(
                It.IsAny<ValidateMeetingCreationRequest>(), null, null, It.IsAny<CancellationToken>()))
            .Throws(new RpcException(new Status(StatusCode.Unavailable, "down")));

        var sut = new WorkspaceMeetingPolicyGrpcClient(
            client.Object, NullLogger<WorkspaceMeetingPolicyGrpcClient>.Instance);

        var result = await sut.ValidateMeetingCreationAsync(
            WorkspaceId, Guid.NewGuid(), new[] { "vi" });

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ServiceUnavailable, result.ErrorCode);
    }

    private static GetWorkspacePreflightResponse Preflight(bool isActive) => new()
    {
        IsActive = isActive,
        WorkspaceName = "Acme",
        WorkspaceSlug = "acme"
    };

    private static WorkspaceMeetingPolicyGrpcClient Create(GetWorkspacePreflightResponse response)
    {
        var client = new Mock<WorkspaceService.WorkspaceServiceClient>();
        client.Setup(c => c.GetWorkspacePreflightDetailsAsync(
                It.IsAny<GetWorkspacePreflightRequest>(), null, null, It.IsAny<CancellationToken>()))
            .Returns(AsyncUnary(response));

        return new WorkspaceMeetingPolicyGrpcClient(
            client.Object, NullLogger<WorkspaceMeetingPolicyGrpcClient>.Instance);
    }

    private static AsyncUnaryCall<T> AsyncUnary<T>(T value) => new(
        Task.FromResult(value),
        Task.FromResult(new Metadata()),
        () => Status.DefaultSuccess,
        () => new Metadata(),
        () => { });
}
