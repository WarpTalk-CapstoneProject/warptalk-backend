using System.Linq.Expressions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.API.Workers;
using WarpTalk.TranslationRoomService.Application.Helpers;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Domain.Constants;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using static WarpTalk.TranslationRoomService.Tests.Workers.RoomReaperFixtures;

namespace WarpTalk.TranslationRoomService.Tests.Workers;

/// <summary>
/// The 5-minute abandoned-room sweep, driven one sweep at a time through SweepAsync (internal, see
/// InternalsVisibleTo in the API project).
///
/// The repository is faked with BOTH counts, and they disagree for a bridge room: the seat count
/// includes the far-side stand-in, the people count does not. A sweep that still read seats would
/// see the stand-in, answer Leave, and fail these tests — which is the bug. The SQL form of the
/// people count is pinned against Postgres in RoomOccupancyCountTests.
/// </summary>
public sealed class AbandonedRoomSweepWorkerTests
{
    private static string EmptyKey(TranslationRoom room) => $"translationRoom:{room.Id}:empty_since";

    private static readonly TimeSpan PastGrace = AbandonedRoomPolicy.GracePeriod + TimeSpan.FromMinutes(1);

    // ── EXTERNAL_BRIDGE ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(TranslationRoomParticipantStatuses.Left)]
    [InlineData(TranslationRoomParticipantStatuses.Disconnected)]
    public async Task ABridgeRoomWhoseHostIsGone_StartsItsGraceOnTheFirstSweep(string hostStatus)
    {
        var room = Room(TranslationRoomTypes.ExternalBridge);
        var harness = new Harness(room, Host(room, hostStatus), StandIn(room));

        await harness.SweepAsync();

        harness.Ended.Should().BeEmpty();
        harness.Redis.Has(EmptyKey(room)).Should().BeTrue(
            "the stand-in's seat must not read as somebody present — before the fix no grace ever started");
    }

    [Theory]
    [InlineData(TranslationRoomParticipantStatuses.Left)]
    [InlineData(TranslationRoomParticipantStatuses.Disconnected)]
    public async Task ABridgeRoomWhoseHostIsGone_IsEndedOnceTheGraceHasRun(string hostStatus)
    {
        var room = Room(TranslationRoomTypes.ExternalBridge);
        var harness = new Harness(room, Host(room, hostStatus), StandIn(room));
        harness.Redis.ObservedEmpty(EmptyKey(room), PastGrace);

        await harness.SweepAsync();

        harness.Ended.Should().Equal(room.Id);
        harness.Redis.Has(EmptyKey(room)).Should().BeFalse();
    }

    [Fact]
    public async Task ABridgeRoomWithItsHostStillIn_IsLeftAlone()
    {
        var room = Room(TranslationRoomTypes.ExternalBridge);
        var harness = new Harness(room, Host(room, TranslationRoomParticipantStatuses.Connected), StandIn(room));
        harness.Redis.ObservedEmpty(EmptyKey(room), PastGrace);

        await harness.SweepAsync();

        harness.Ended.Should().BeEmpty();
        harness.Redis.Has(EmptyKey(room)).Should().BeFalse("the host came back, so the old observation is void");
    }

    // ── Native rooms (regression) ────────────────────────────────────────────

    [Fact]
    public async Task ANativeRoomWithSomebodyInIt_IsLeftAlone()
    {
        var room = Room();
        var harness = new Harness(room, Host(room, TranslationRoomParticipantStatuses.Connected));
        harness.Redis.ObservedEmpty(EmptyKey(room), PastGrace);

        await harness.SweepAsync();

        harness.Ended.Should().BeEmpty();
    }

    [Fact]
    public async Task ANativeRoomNobodyIsIn_IsStillEndedAfterTheGrace()
    {
        var room = Room();
        var harness = new Harness(room, Host(room, TranslationRoomParticipantStatuses.Disconnected));
        harness.Redis.ObservedEmpty(EmptyKey(room), PastGrace);

        await harness.SweepAsync();

        harness.Ended.Should().Equal(room.Id);
    }

    [Fact]
    public async Task ANativeRoomNobodyIsIn_IsNotEndedInsideTheGrace()
    {
        var room = Room();
        var harness = new Harness(room, Host(room, TranslationRoomParticipantStatuses.Left));
        harness.Redis.ObservedEmpty(EmptyKey(room), TimeSpan.FromMinutes(5));

        await harness.SweepAsync();

        harness.Ended.Should().BeEmpty();
    }

    private sealed class Harness
    {
        private readonly AbandonedRoomSweepWorker _worker;

        public FakeRedisKeys Redis { get; } = new();
        public List<Guid> Ended { get; } = new();

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
                .Setup(r => r.CountSeatHoldingParticipantsByRoomsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((IReadOnlyCollection<Guid> ids, CancellationToken _) =>
                    Count(ids, p => TranslationRoomParticipantStatuses.HoldsSeat(p.Status)));
            participantRepository
                .Setup(r => r.CountPeopleInRoomsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((IReadOnlyCollection<Guid> ids, CancellationToken _) =>
                    Count(ids, RoomPresence.IsPersonInRoom));

            var unitOfWork = new Mock<IUnitOfWork>();
            unitOfWork.SetupGet(u => u.TranslationRoomRepository).Returns(roomRepository.Object);
            unitOfWork.SetupGet(u => u.TranslationRoomParticipantRepository).Returns(participantRepository.Object);

            var roomService = new Mock<ITranslationRoomService>();
            roomService
                .Setup(s => s.EndTranslationRoomAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid roomId, Guid _, CancellationToken _) =>
                {
                    Ended.Add(roomId);
                    return Result.Success();
                });

            var services = new ServiceCollection();
            services.AddScoped(_ => unitOfWork.Object);
            services.AddScoped(_ => roomService.Object);

            _worker = new AbandonedRoomSweepWorker(
                services.BuildServiceProvider(),
                Redis.Multiplexer,
                NullLogger<AbandonedRoomSweepWorker>.Instance);

            Dictionary<Guid, int> Count(IReadOnlyCollection<Guid> ids, Func<TranslationRoomParticipant, bool> counts) =>
                participants
                    .Where(p => ids.Contains(p.TranslationRoomId) && counts(p))
                    .GroupBy(p => p.TranslationRoomId)
                    .ToDictionary(g => g.Key, g => g.Count());
        }

        public Task SweepAsync() => _worker.SweepAsync(CancellationToken.None);
    }
}
