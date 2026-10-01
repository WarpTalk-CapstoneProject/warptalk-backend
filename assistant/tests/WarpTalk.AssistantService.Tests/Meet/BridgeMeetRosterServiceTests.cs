using NSubstitute;
using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Application.Services;
using WarpTalk.Shared;

namespace WarpTalk.AssistantService.Tests.Meet;

public class BridgeMeetRosterServiceTests
{
    private static readonly Guid RoomId = Guid.NewGuid();
    private static readonly Guid HostId = Guid.NewGuid();
    private static readonly Guid MemberId = Guid.NewGuid();
    private const string MeetUrl = "https://meet.google.com/abc-mnop-xyz";

    private readonly IBridgeRoomDirectory _rooms = Substitute.For<IBridgeRoomDirectory>();
    private readonly IHostMeetConferenceService _meet = Substitute.For<IHostMeetConferenceService>();

    private BridgeMeetRosterService Sut() => new(_rooms, _meet);

    private void Room(bool bridge = true, string? url = MeetUrl) =>
        _rooms.GetAsync(RoomId, Arg.Any<CancellationToken>())
            .Returns((BridgeRoomLookupStatus.Found, new BridgeRoomInfo(RoomId, bridge, url, HostId, HostId, new[] { HostId, MemberId })));

    [Fact]
    public async Task Participant_GetsTheRoster_ReadWithTheHostsGrant()
    {
        Room();
        _meet.GetRosterAsync(HostId, MeetUrl, Arg.Any<CancellationToken>()).Returns(Result.Success(new MeetRosterDto(
            "conferenceRecords/r1",
            [new MeetParticipantDto("conferenceRecords/r1/participants/1", "Alice", MeetParticipantKinds.SignedIn, null, null)])));

        var result = await Sut().GetAsync(RoomId, MemberId);

        Assert.True(result.IsSuccess);
        Assert.Equal("Alice", Assert.Single(result.Value!).DisplayName);
    }

    [Fact]
    public async Task Outsider_IsForbidden_AndGoogleIsNotAsked()
    {
        Room();

        var result = await Sut().GetAsync(RoomId, Guid.NewGuid());

        Assert.Equal(BridgeMeetRosterService.ErrorCodes.Forbidden, result.ErrorCode);
        await _meet.DidNotReceiveWithAnyArgs().GetRosterAsync(default, default!, default);
    }

    [Fact]
    public async Task UnknownRoom_IsNotFound()
    {
        _rooms.GetAsync(RoomId, Arg.Any<CancellationToken>()).Returns((BridgeRoomLookupStatus.NotFound, (BridgeRoomInfo?)null));

        var result = await Sut().GetAsync(RoomId, HostId);

        Assert.Equal(BridgeMeetRosterService.ErrorCodes.RoomNotFound, result.ErrorCode);
    }

    [Fact]
    public async Task OrdinaryRoom_IsNotABridge()
    {
        Room(bridge: false, url: null);

        var result = await Sut().GetAsync(RoomId, HostId);

        Assert.Equal(BridgeMeetRosterService.ErrorCodes.NotABridgeRoom, result.ErrorCode);
    }

    [Fact]
    public async Task HostWithoutTheScope_PassesMeetScopeMissingThrough()
    {
        Room();
        _meet.GetRosterAsync(HostId, MeetUrl, Arg.Any<CancellationToken>())
            .Returns(Result.Failure<MeetRosterDto>("no scope", MeetConferenceErrorCodes.MeetScopeMissing));

        var result = await Sut().GetAsync(RoomId, HostId);

        Assert.Equal(MeetConferenceErrorCodes.MeetScopeMissing, result.ErrorCode);
    }
}
