using System.Linq.Expressions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WarpTalk.Shared;
using WarpTalk.Shared.Coordination;
using WarpTalk.TranslationRoomService.API.Workers;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Interfaces;

namespace WarpTalk.TranslationRoomService.Tests.Workers;

/// <summary>
/// WT-612 / WT-714. The booking clock: the sweep that opens a booking when its time comes and
/// expires one nobody ever came to, driven through SweepAsync (internal — see InternalsVisibleTo)
/// so the repository predicates run for real.
///
/// What these tests are guarding is not "the worker calls the service": it is that the clock only
/// touches rooms whose slot has genuinely arrived or genuinely passed, that it never does both to
/// the same room, and that one room it cannot move does not cost the rest of the sweep.
/// </summary>
public sealed class ScheduledRoomLifecycleWorkerTests
{
    [Fact]
    public async Task Sweep_OpensABookingWhoseSlotHasArrived()
    {
        var due = Room("SCHEDULED", DateTime.UtcNow.AddMinutes(-1));
        var harness = new Harness(due);

        await harness.PollAsync();

        harness.Opened.Should().Equal(due.Id);
        harness.Expired.Should().BeEmpty();
    }

    [Fact]
    public async Task Sweep_LeavesABookingThatIsNotDueYet()
    {
        var harness = new Harness(Room("SCHEDULED", DateTime.UtcNow.AddMinutes(10)));

        await harness.PollAsync();

        harness.Opened.Should().BeEmpty();
        harness.Expired.Should().BeEmpty();
    }

    [Fact]
    public async Task Sweep_ExpiresRatherThanOpens_ABookingWhoseGraceHasRunOut()
    {
        // A worker that has been down for a day must not come back and fling open every booking
        // anybody ever made. Those rooms are missed meetings, and this same pass says so.
        var stale = Room("SCHEDULED", DateTime.UtcNow.AddHours(-5));
        var harness = new Harness(stale);

        await harness.PollAsync();

        harness.Opened.Should().BeEmpty();
        harness.Expired.Should().Equal(stale.Id);
    }

    [Fact]
    public async Task Sweep_ExpiresABookingTheClockOpenedAndNobodyAttended()
    {
        // The status WT-612 introduced is the one that would otherwise stand open forever: the
        // door was unlocked at 14:00 and nobody ever walked through it.
        var unattended = Room("OPEN", DateTime.UtcNow.AddHours(-3));
        var harness = new Harness(unattended);

        await harness.PollAsync();

        harness.Expired.Should().Equal(unattended.Id);
    }

    [Fact]
    public async Task Sweep_HandlesBothEdgesInOneTick()
    {
        // The reason the two halves share a worker and a lease: yesterday's missed booking and
        // today's due one are both waiting for the same minute.
        var due = Room("SCHEDULED", DateTime.UtcNow.AddMinutes(-1));
        var stale = Room("SCHEDULED", DateTime.UtcNow.AddHours(-4));
        var harness = new Harness(due, stale);

        await harness.PollAsync();

        harness.Opened.Should().Equal(due.Id);
        harness.Expired.Should().Equal(stale.Id);
    }

    [Fact]
    public async Task Sweep_LeavesARoomTheHostAlreadyActedOn()
    {
        var harness = new Harness(
            Room("WAITING", DateTime.UtcNow.AddMinutes(-1)),
            Room("IN_PROGRESS", DateTime.UtcNow.AddMinutes(-1)),
            Room("CANCELLED", DateTime.UtcNow.AddMinutes(-1)),
            Room("OPEN", DateTime.UtcNow.AddMinutes(-1)));

        await harness.PollAsync();

        harness.Opened.Should().BeEmpty();
        harness.Expired.Should().BeEmpty();
    }

    [Fact]
    public async Task Sweep_LeavesAMeetingSomebodyActuallyJoined_HoweverLongItRuns()
    {
        // The expiry rule needs no participant check precisely because attendance is already
        // written in the status. A three-hour meeting is a room in progress, not a missed booking.
        var harness = new Harness(
            Room("IN_PROGRESS", DateTime.UtcNow.AddHours(-3)),
            Room("PAUSED", DateTime.UtcNow.AddHours(-4)),
            Room("ENDED", DateTime.UtcNow.AddHours(-6)),
            Room("CANCELLED", DateTime.UtcNow.AddHours(-6)),
            Room("EXPIRED", DateTime.UtcNow.AddHours(-6)));

        await harness.PollAsync();

        harness.Opened.Should().BeEmpty();
        harness.Expired.Should().BeEmpty();
    }

    [Fact]
    public async Task Sweep_KeepsGoing_WhenOneRoomCannotBeOpened()
    {
        // The realistic failure: the host started the room in the seconds between the query and
        // the call, so the service refuses it. Every other booking due this minute must still
        // open — a meeting nobody can get into is exactly the complaint this change exists for.
        var refused = Room("SCHEDULED", DateTime.UtcNow.AddMinutes(-2));
        var second = Room("SCHEDULED", DateTime.UtcNow.AddMinutes(-1));
        var harness = new Harness(refused, second);
        harness.RefuseFor.Add(refused.Id);

        await harness.PollAsync();

        harness.Opened.Should().Equal(second.Id);
    }

    [Fact]
    public async Task Sweep_KeepsGoing_WhenOneRoomCannotBeExpired()
    {
        var refused = Room("SCHEDULED", DateTime.UtcNow.AddHours(-5));
        var second = Room("OPEN", DateTime.UtcNow.AddHours(-4));
        var harness = new Harness(refused, second);
        harness.RefuseFor.Add(refused.Id);

        await harness.PollAsync();

        harness.Expired.Should().Equal(second.Id);
    }

    [Fact]
    public async Task Sweep_StillExpiresStaleBookings_WhenAnOpenIsRefused()
    {
        // The two halves share a tick; they must not share a failure. A refusal in the opening
        // pass cannot be allowed to leave yesterday's missed bookings standing.
        var refusedOpen = Room("SCHEDULED", DateTime.UtcNow.AddMinutes(-1));
        var stale = Room("SCHEDULED", DateTime.UtcNow.AddHours(-5));
        var harness = new Harness(refusedOpen, stale);
        harness.RefuseFor.Add(refusedOpen.Id);

        await harness.PollAsync();

        harness.Opened.Should().BeEmpty();
        harness.Expired.Should().Equal(stale.Id);
    }

    private static TranslationRoom Room(string status, DateTime scheduledAt) => new()
    {
        Id = Guid.NewGuid(),
        WorkspaceId = Guid.NewGuid(),
        HostId = Guid.NewGuid(),
        Title = "Weekly sync",
        TranslationRoomCode = "WKL-4821",
        Status = status,
        TranslationRoomType = "SCHEDULED",
        SourceLanguage = "en",
        TargetLanguages = "[\"vi\"]",
        Settings = "{}",
        ScheduledAt = scheduledAt,
    };

    private sealed class Harness
    {
        private readonly ScheduledRoomLifecycleWorker _worker;

        public List<Guid> Opened { get; } = new();

        public List<Guid> Expired { get; } = new();

        /// <summary>Rooms the service refuses, whichever edge asks for them.</summary>
        public HashSet<Guid> RefuseFor { get; } = new();

        public Harness(params TranslationRoom[] rooms)
        {
            var roomRepository = new Mock<ITranslationRoomRepository>();
            roomRepository
                .Setup(r => r.FindAsync(
                    It.IsAny<Expression<Func<TranslationRoom, bool>>>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((Expression<Func<TranslationRoom, bool>> predicate, string _, CancellationToken _) =>
                    (IReadOnlyList<TranslationRoom>)rooms.Where(predicate.Compile()).ToList());

            var unitOfWork = new Mock<IUnitOfWork>();
            unitOfWork.SetupGet(u => u.TranslationRoomRepository).Returns(roomRepository.Object);

            var roomService = new Mock<ITranslationRoomService>();
            roomService
                .Setup(s => s.OpenScheduledRoomAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid roomId, CancellationToken _) =>
                {
                    if (RefuseFor.Contains(roomId))
                        return Result.Failure("Room must be SCHEDULED to open at its scheduled time.", ErrorCodes.InvalidState);

                    Opened.Add(roomId);
                    return Result.Success();
                });
            roomService
                .Setup(s => s.ExpireTranslationRoomAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid roomId, CancellationToken _) =>
                {
                    if (RefuseFor.Contains(roomId))
                        return Result.Failure("Room must be SCHEDULED or WAITING to expire.", ErrorCodes.InvalidState);

                    Expired.Add(roomId);
                    return Result.Success();
                });

            var services = new ServiceCollection();
            services.AddScoped(_ => unitOfWork.Object);
            services.AddScoped(_ => roomService.Object);

            _worker = new ScheduledRoomLifecycleWorker(
                services.BuildServiceProvider(),
                Mock.Of<IDistributedLockProvider>(),
                NullLogger<ScheduledRoomLifecycleWorker>.Instance);
        }

        public Task PollAsync() => _worker.SweepAsync(CancellationToken.None);
    }
}
