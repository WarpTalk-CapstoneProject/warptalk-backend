using System.Security.Claims;
using Grpc.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using WarpTalk.AssistantService.API.Controllers;
using WarpTalk.AssistantService.API.GrpcServices;
using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Application.Services;
using WarpTalk.Shared;
using WarpTalk.Shared.Protos;

namespace WarpTalk.AssistantService.Tests.Meet;

/// <summary>
/// The plugin_not_connected contract at both edges: the roster endpoint answers 409 with the code
/// web branches on, and the gRPC surface forwards the room's workspace and reports the code in band.
/// </summary>
public class MeetPluginNotConnectedContractTests
{
    private static readonly Guid RoomId = Guid.NewGuid();
    private static readonly Guid CallerId = Guid.NewGuid();
    private static readonly Guid HostId = Guid.NewGuid();
    private static readonly Guid WorkspaceId = Guid.NewGuid();
    private const string Meeting = "abc-mnop-xyz";

    private static BridgeMeetParticipantsController Controller(IBridgeMeetRosterService roster) => new(roster)
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, CallerId.ToString())], "test")),
            },
        },
    };

    [Theory]
    [InlineData(MeetConferenceErrorCodes.PluginNotConnected)]
    [InlineData(MeetConferenceErrorCodes.ConnectionRequired)]
    [InlineData(MeetConferenceErrorCodes.MeetScopeMissing)]
    public async Task Roster_HostCannotBeReadFor_Is409WithTheCode(string code)
    {
        var roster = Substitute.For<IBridgeMeetRosterService>();
        roster.GetAsync(RoomId, CallerId, Arg.Any<CancellationToken>())
            .Returns(Result.Failure<IReadOnlyList<MeetParticipantDto>>("nope", code));

        var response = await Controller(roster).GetMeetParticipants(RoomId, CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(response);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
        var errorCode = conflict.Value!.GetType().GetProperty("errorCode")!.GetValue(conflict.Value);
        Assert.Equal(code, errorCode);
    }

    [Fact]
    public async Task Grpc_ForwardsTheWorkspace_AndReportsPluginNotConnectedInBand()
    {
        var meet = Substitute.For<IHostMeetConferenceService>();
        meet.GetConferenceRecordsAsync(HostId, WorkspaceId, Meeting, Arg.Any<CancellationToken>())
            .Returns(Result.Failure<IReadOnlyList<MeetConferenceRecordDto>>("not connected", MeetConferenceErrorCodes.PluginNotConnected));
        meet.GetMeetTranscriptEntriesAsyncFails(HostId, WorkspaceId, Meeting);

        var service = new MeetConferenceGrpcService(meet);
        var records = await service.GetConferenceRecords(
            new MeetConferenceLookupRequest { UserId = HostId.ToString(), Meeting = Meeting, WorkspaceId = WorkspaceId.ToString() },
            new TestServerCallContext());
        var entries = await service.GetMeetTranscriptEntries(
            new GetMeetTranscriptEntriesRequest
            {
                UserId = HostId.ToString(),
                Meeting = Meeting,
                WorkspaceId = WorkspaceId.ToString(),
                WindowStart = "2026-10-01T09:00:00Z",
                WindowEnd = "2026-10-01T10:00:00Z",
            },
            new TestServerCallContext());

        Assert.Equal(MeetConferenceErrorCodes.PluginNotConnected, records.ErrorCode);
        Assert.Empty(records.Records);
        Assert.Equal(MeetConferenceErrorCodes.PluginNotConnected, entries.ErrorCode);
        Assert.Empty(entries.Entries);
    }

    [Fact]
    public void Grpc_EmptyWorkspaceIsAnOlderCaller_AMalformedOneIsRejected()
    {
        Assert.Null(MeetConferenceGrpcService.ParseWorkspace(""));
        Assert.Equal(WorkspaceId, MeetConferenceGrpcService.ParseWorkspace(WorkspaceId.ToString()));
        var ex = Assert.Throws<RpcException>(() => MeetConferenceGrpcService.ParseWorkspace("not-a-guid"));
        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    [Fact]
    public void TheCodeIsTheCrossServiceContract()
    {
        // translation-room's end worker, transcript's relabel job and web all branch on this string.
        Assert.Equal("plugin_not_connected", MeetConferenceErrorCodes.PluginNotConnected);
    }
}

internal static class HostMeetConferenceServiceSubstituteExtensions
{
    public static void GetMeetTranscriptEntriesAsyncFails(this IHostMeetConferenceService meet, Guid host, Guid workspace, string meeting) =>
        meet.GetTranscriptEntriesAsync(host, workspace, meeting, Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<MeetTranscriptEntriesDto>("not connected", MeetConferenceErrorCodes.PluginNotConnected));
}

internal sealed class TestServerCallContext : ServerCallContext
{
    protected override string MethodCore => "TestMethod";
    protected override string HostCore => "localhost";
    protected override string PeerCore => "127.0.0.1";
    protected override DateTime DeadlineCore => DateTime.MaxValue;
    protected override Metadata RequestHeadersCore => new();
    protected override CancellationToken CancellationTokenCore => CancellationToken.None;
    protected override Metadata ResponseTrailersCore => new();
    protected override Status StatusCore { get; set; }
    protected override WriteOptions? WriteOptionsCore { get; set; }
    protected override AuthContext AuthContextCore => null!;

    protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) => null!;
    protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
}
