using System.Linq.Expressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WarpTalk.Shared;
using WarpTalk.Shared.Coordination;
using WarpTalk.TranslationRoomService.API.Workers;
using WarpTalk.TranslationRoomService.Application.Helpers;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Interfaces;

namespace WarpTalk.TranslationRoomService.Tests.Workers;

public sealed class MeetConferenceEndPolicyTests
{
    private static readonly DateTime Created = new(2026, 10, 1, 8, 55, 0, DateTimeKind.Utc);
    private static readonly DateTime Started = new(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);

    private static MeetConferenceRecordInfo Record(string name, DateTime start, DateTime? end) => new(name, start, end);

    [Fact]
    public void NoConferenceYet_KeepsTheRoom()
    {
        Assert.Equal(MeetConferenceEndVerdict.NoConference, MeetConferenceEndPolicy.Decide([], Created, Started));
    }

    [Fact]
    public void LiveConference_KeepsTheRoom()
    {
        var verdict = MeetConferenceEndPolicy.Decide([Record("r1", Started, null)], Created, Started);

        Assert.Equal(MeetConferenceEndVerdict.ConferenceLive, verdict);
    }

    [Fact]
    public void ConferenceEndedAfterTheRoomStarted_EndsTheRoom()
    {
        var verdict = MeetConferenceEndPolicy.Decide([Record("r1", Started.AddMinutes(-2), Started.AddMinutes(45))], Created, Started);

        Assert.Equal(MeetConferenceEndVerdict.End, verdict);
    }

    [Fact]
    public void AnEndedRecordAndANewerLiveOne_KeepsTheRoom()
    {
        // Everybody left and came back: Google opened a second record for the same code.
        var verdict = MeetConferenceEndPolicy.Decide(
            [Record("r1", Started, Started.AddMinutes(10)), Record("r2", Started.AddMinutes(12), null)],
            Created,
            Started);

        Assert.Equal(MeetConferenceEndVerdict.ConferenceLive, verdict);
    }

    [Fact]
    public void OnlyAnEarlierMeetingOnTheSameCode_KeepsTheRoom()
    {
        // A recurring event reuses its Meet code: yesterday's conference is over, today's has not
        // started. The room must not be ended for yesterday's.
        var verdict = MeetConferenceEndPolicy.Decide(
            [Record("yesterday", Started.AddDays(-1), Started.AddDays(-1).AddHours(1))],
            Created,
            Started);

        Assert.Equal(MeetConferenceEndVerdict.EndedBeforeRoom, verdict);
    }

    [Fact]
    public void RecordEndedBetweenCreationAndStart_BelongsToAnEarlierMeeting()
    {
        var verdict = MeetConferenceEndPolicy.Decide([Record("r0", Created.AddMinutes(-30), Created.AddMinutes(2))], Created, Started);

        Assert.Equal(MeetConferenceEndVerdict.EndedBeforeRoom, verdict);
    }

    [Fact]
    public void RoomNotStartedYet_IsMeasuredFromItsCreation()
    {
        Assert.Equal(
            MeetConferenceEndVerdict.End,
            MeetConferenceEndPolicy.Decide([Record("r1", Created, Created.AddMinutes(1))], Created, null));
        Assert.Equal(
            MeetConferenceEndVerdict.EndedBeforeRoom,
            MeetConferenceEndPolicy.Decide([Record("r1", Created.AddHours(-1), Created.AddMinutes(-1))], Created, null));
    }

    [Fact]
    public void TheLatestEndCounts_NotTheFirstRecord()
    {
        var verdict = MeetConferenceEndPolicy.Decide(
            [Record("old", Started.AddDays(-7), Started.AddDays(-7).AddHours(1)), Record("today", Started, Started.AddHours(1))],
            Created,
            Started);

        Assert.Equal(MeetConferenceEndVerdict.End, verdict);
    }
}

public sealed class MeetConferenceEndWorkerTests
{
    private const string MeetUrl = "https://meet.google.com/abc-mnop-xyz";
    private readonly Mock<ITranslationRoomRepository> _rooms = new();
    private readonly Mock<ITranslationRoomService> _roomService = new();
    private readonly Mock<IMeetConferenceRecordsClient> _meet = new();

    private MeetConferenceEndWorker Worker()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_rooms.Object);
        services.AddSingleton(_roomService.Object);
        services.AddSingleton(_meet.Object);
        return new MeetConferenceEndWorker(
            services.BuildServiceProvider(),
            new DistributedLockProvider(new InProcessLeaseStore(TimeProvider.System), TimeProvider.System),
            NullLogger<MeetConferenceEndWorker>.Instance);
    }

    private TranslationRoom Room(Guid? activeHost = null)
    {
        var room = new TranslationRoom
        {
            Id = Guid.NewGuid(),
            HostId = Guid.NewGuid(),
            ActiveHostId = activeHost,
            Status = "IN_PROGRESS",
            TranslationRoomType = ExternalBridgeConstants.RoomType,
            ExternalMeetingUrl = MeetUrl,
            CreatedAt = DateTime.UtcNow.AddHours(-1),
            StartedAt = DateTime.UtcNow.AddHours(-1),
        };
        _rooms.Setup(r => r.FindAsync(It.IsAny<Expression<Func<TranslationRoom, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TranslationRoom> { room });
        _roomService.Setup(s => s.EndTranslationRoomAsync(room.Id, room.HostId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
        return room;
    }

    private void MeetAnswers(Guid userId, MeetConferenceRecordsLookup lookup) =>
        _meet.Setup(m => m.GetRecordsAsync(userId, MeetUrl, It.IsAny<CancellationToken>())).ReturnsAsync(lookup);

    [Fact]
    public async Task EndedConference_EndsTheRoomThroughTheOrdinaryEndPath_AskingWithTheEffectiveHost()
    {
        var newHost = Guid.NewGuid();
        var room = Room(activeHost: newHost);
        MeetAnswers(newHost, new MeetConferenceRecordsLookup(null,
            [new MeetConferenceRecordInfo("r1", DateTime.UtcNow.AddMinutes(-50), DateTime.UtcNow.AddMinutes(-1))]));

        await Worker().CheckAsync(CancellationToken.None);

        _roomService.Verify(s => s.EndTranslationRoomAsync(room.Id, room.HostId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task LiveConference_LeavesTheRoomOpen()
    {
        var room = Room();
        MeetAnswers(room.HostId, new MeetConferenceRecordsLookup(null,
            [new MeetConferenceRecordInfo("r1", DateTime.UtcNow.AddMinutes(-50), null)]));

        await Worker().CheckAsync(CancellationToken.None);

        _roomService.Verify(s => s.EndTranslationRoomAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(MeetConferenceErrorCodes.MeetScopeMissing)]
    [InlineData(MeetConferenceErrorCodes.ConnectionRequired)]
    [InlineData(MeetConferenceErrorCodes.ProviderUnavailable)]
    public async Task HostWithoutAccessOrGoogleDown_LeavesTheRoomToTheOtherReapers(string errorCode)
    {
        var room = Room();
        MeetAnswers(room.HostId, new MeetConferenceRecordsLookup(errorCode, []));

        var worker = Worker();
        await worker.CheckAsync(CancellationToken.None);
        await worker.CheckAsync(CancellationToken.None);

        _roomService.Verify(s => s.EndTranslationRoomAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
