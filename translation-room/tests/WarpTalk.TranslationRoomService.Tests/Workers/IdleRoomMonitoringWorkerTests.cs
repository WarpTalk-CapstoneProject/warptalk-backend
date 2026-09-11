using System.Linq.Expressions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.API.Workers;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Domain.Constants;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using static WarpTalk.TranslationRoomService.Tests.Workers.RoomReaperFixtures;

namespace WarpTalk.TranslationRoomService.Tests.Workers;

/// <summary>
/// The 1-minute idle reaper, driven one tick at a time through CheckAndEndIdleRoomsAsync (internal,
/// see InternalsVisibleTo in the API project).
///
/// Two defects, and the second is what makes fixing the first safe:
///
///   * An EXTERNAL_BRIDGE room's far-side stand-in is seeded CONNECTED and never leaves that
///     status short of End, so it counted as somebody present and the reaper never fired. A bridge
///     host who pressed Leave, or whose app crashed, left the room IN_PROGRESS forever.
///
///   * The idle clock was anchored to the participants' last LeftAt, falling back to JoinedAt. A
///     dropped socket writes DISCONNECTED and no LeftAt, so a sole participant an hour into a
///     meeting was "idle since they joined" and ended on the very next tick after any blip. Once
///     the stand-in stops counting, that is every bridge host.
/// </summary>
public sealed class IdleRoomMonitoringWorkerTests
{
    private static string IdleKey(TranslationRoom room) => $"translationRoom:{room.Id}:idle_since";

    // ── EXTERNAL_BRIDGE ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(TranslationRoomParticipantStatuses.Left)]
    [InlineData(TranslationRoomParticipantStatuses.Disconnected)]
    public async Task ABridgeRoomWhoseHostIsGone_IsEndedOnceTheGraceHasRun(string hostStatus)
    {
        var room = Room(TranslationRoomTypes.ExternalBridge);
        var harness = new Harness(room, Host(room, hostStatus), StandIn(room));
        harness.Redis.ObservedEmpty(IdleKey(room), TimeSpan.FromMinutes(6));

        await harness.TickAsync();

        harness.Ended.Should().Equal(new[] { room.Id },
            "the stand-in holds a seat but is not a person — a room with only it in is empty");
        harness.Redis.Has(IdleKey(room)).Should().BeFalse("an ended room leaves no observation behind");
    }

    [Theory]
    [InlineData(TranslationRoomParticipantStatuses.Left)]
    [InlineData(TranslationRoomParticipantStatuses.Disconnected)]
    public async Task ABridgeRoomWhoseHostIsGone_IsNotEndedOnTheFirstTickThatSeesItEmpty(string hostStatus)
    {
        // The JoinedAt trap. The host joined an hour ago and has no LeftAt when DISCONNECTED, so
        // the old anchor put the room five minutes past its timeout the instant the socket dropped.
        var room = Room(TranslationRoomTypes.ExternalBridge);
        var harness = new Harness(room, Host(room, hostStatus), StandIn(room));

        await harness.TickAsync();

        harness.Ended.Should().BeEmpty("the grace starts when a tick first sees the room empty");
        harness.Redis.Has(IdleKey(room)).Should().BeTrue();
    }

    [Fact]
    public async Task ABridgeRoomWhoseHostIsGone_IsNotEndedWhileTheGraceIsRunning()
    {
        var room = Room(TranslationRoomTypes.ExternalBridge);
        var harness = new Harness(room, Host(room, TranslationRoomParticipantStatuses.Disconnected), StandIn(room));
        harness.Redis.ObservedEmpty(IdleKey(room), TimeSpan.FromMinutes(3));

        await harness.TickAsync();

        harness.Ended.Should().BeEmpty();
    }

    [Fact]
    public async Task ABridgeHostWhoCameBack_ResetsTheGrace()
    {
        // A blip: an earlier tick saw the room empty, then the socket returned and the row went
        // back to CONNECTED. The observation must go, or the next blip would inherit it and end
        // the meeting at once.
        var room = Room(TranslationRoomTypes.ExternalBridge);
        var harness = new Harness(room, Host(room, TranslationRoomParticipantStatuses.Connected), StandIn(room));
        harness.Redis.ObservedEmpty(IdleKey(room), TimeSpan.FromMinutes(10));

        await harness.TickAsync();

        harness.Ended.Should().BeEmpty();
        harness.Redis.Has(IdleKey(room)).Should().BeFalse();
    }

    // ── Native rooms (regression) ────────────────────────────────────────────

    [Fact]
    public async Task ANativeRoomWithSomebodyInIt_IsLeftAlone()
    {
        var room = Room();
        var harness = new Harness(room, Host(room, TranslationRoomParticipantStatuses.Connected));

        await harness.TickAsync();

        harness.Ended.Should().BeEmpty();
        harness.Redis.Has(IdleKey(room)).Should().BeFalse();
    }

    [Fact]
    public async Task ANativeRoomsSoleParticipantWhoDropped_GetsTheSameGrace()
    {
        // The same trap was never bridge-specific: any sole participant who had been in the room
        // for more than five minutes lost the meeting to one dropped socket.
        var room = Room();
        var harness = new Harness(room, Host(room, TranslationRoomParticipantStatuses.Disconnected));

        await harness.TickAsync();

        harness.Ended.Should().BeEmpty();
    }

    [Fact]
    public async Task ANativeRoomNobodyIsIn_IsStillEndedAfterTheGrace()
    {
        var room = Room();
        var harness = new Harness(room, Host(room, TranslationRoomParticipantStatuses.Left));
        harness.Redis.ObservedEmpty(IdleKey(room), TimeSpan.FromMinutes(6));

        await harness.TickAsync();

        harness.Ended.Should().Equal(room.Id);
    }

    [Fact]
    public async Task AFailedEnd_KeepsTheObservationSoTheNextTickRetries()
    {
        var room = Room(TranslationRoomTypes.ExternalBridge);
        var harness = new Harness(room, Host(room, TranslationRoomParticipantStatuses.Left), StandIn(room));
        harness.Redis.ObservedEmpty(IdleKey(room), TimeSpan.FromMinutes(6));
        harness.EndResult = Result.Failure("boom", ErrorCodes.InternalServerError);

        await harness.TickAsync();

        harness.Redis.Has(IdleKey(room)).Should().BeTrue();
    }

    private sealed class Harness
    {
        private readonly IdleRoomMonitoringWorker _worker;

        public FakeRedisKeys Redis { get; } = new();
        public List<Guid> Ended { get; } = new();
        public Result EndResult { get; set; } = Result.Success();

        public Harness(TranslationRoom room, params TranslationRoomParticipant[] participants)
        {
            var rooms = new[] { room };

            var roomRepository = new Mock<ITranslationRoomRepository>();
            roomRepository
                .Setup(r => r.FindAsync(
                    It.IsAny<Expression<Func<TranslationRoom, bool>>>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((Expression<Func<TranslationRoom, bool>> predicate, string _, CancellationToken _) =>
                    (IReadOnlyList<TranslationRoom>)rooms.Where(predicate.Compile()).ToList());

            var participantRepository = new Mock<ITranslationRoomParticipantRepository>();
            participantRepository
                .Setup(r => r.FindAsync(
                    It.IsAny<Expression<Func<TranslationRoomParticipant, bool>>>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((Expression<Func<TranslationRoomParticipant, bool>> predicate, string _, CancellationToken _) =>
                    (IReadOnlyList<TranslationRoomParticipant>)participants.Where(predicate.Compile()).ToList());

            var roomService = new Mock<ITranslationRoomService>();
            roomService
                .Setup(s => s.EndTranslationRoomAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid roomId, Guid _, CancellationToken _) =>
                {
                    if (EndResult.IsSuccess) Ended.Add(roomId);
                    return EndResult;
                });

            var services = new ServiceCollection();
            services.AddScoped(_ => roomRepository.Object);
            services.AddScoped(_ => participantRepository.Object);
            services.AddScoped(_ => roomService.Object);

            _worker = new IdleRoomMonitoringWorker(
                services.BuildServiceProvider(),
                Redis.Multiplexer,
                NullLogger<IdleRoomMonitoringWorker>.Instance);
        }

        public Task TickAsync() => _worker.CheckAndEndIdleRoomsAsync(CancellationToken.None);
    }
}
