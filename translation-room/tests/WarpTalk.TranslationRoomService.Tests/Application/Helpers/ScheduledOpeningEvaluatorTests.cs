using FluentAssertions;
using WarpTalk.TranslationRoomService.Application.Helpers;
using WarpTalk.TranslationRoomService.Domain.Entities;

namespace WarpTalk.TranslationRoomService.Tests.Application.Helpers;

/// <summary>
/// WT-612 / WT-714. The rule the booking sweep runs on, from both ends: a booking opens when its
/// slot arrives, stops being openable once it is two hours stale, and at that same instant becomes
/// a meeting that did not happen.
///
/// The prefilters are exercised by compiling them, not by restating them — the defect that would
/// hide is a predicate that disagrees with <see cref="ScheduledOpeningEvaluator.ShouldOpen"/> or
/// <see cref="ScheduledOpeningEvaluator.HasExpired"/>, and a test that asserts the same thing
/// twice cannot see that.
/// </summary>
public sealed class ScheduledOpeningEvaluatorTests
{
    private static readonly DateTime Now = new(2026, 9, 24, 14, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ShouldOpen_IsTrue_TheMomentTheSlotArrives()
    {
        ScheduledOpeningEvaluator.ShouldOpen(Now, Now).Should().BeTrue();
    }

    [Fact]
    public void ShouldOpen_IsFalse_BeforeTheSlot()
    {
        // One second early is early. The reminders own the run-up to a meeting; this transition
        // owns the moment it is due.
        ScheduledOpeningEvaluator.ShouldOpen(Now.AddSeconds(1), Now).Should().BeFalse();
    }

    [Fact]
    public void ShouldOpen_IsTrue_ForABookingThatIsLateButInsideTheTolerance()
    {
        // The case that matters after a deploy or a short outage: the worker was not running at
        // 14:00 and comes back at 15:00, and the meeting is still happening.
        ScheduledOpeningEvaluator.ShouldOpen(Now.AddHours(-1), Now).Should().BeTrue();
    }

    [Fact]
    public void ShouldOpen_IsFalse_OnceTheBookingIsStale()
    {
        // Past the tolerance the booking is a meeting that did not happen. Opening it here would
        // contradict the web, which has already been calling it Missed, and the meeting service,
        // which has already stopped honouring its join link.
        ScheduledOpeningEvaluator
            .ShouldOpen(Now - ScheduledOpeningEvaluator.MissedBookingGrace.Add(TimeSpan.FromMinutes(1)), Now)
            .Should().BeFalse();
    }

    [Fact]
    public void HasExpired_IsFalse_ForABookingStillInsideItsGrace()
    {
        ScheduledOpeningEvaluator.HasExpired(Now.AddHours(-1), Now).Should().BeFalse();
    }

    [Fact]
    public void HasExpired_IsTrue_OnceTheGraceHasRunOut()
    {
        ScheduledOpeningEvaluator
            .HasExpired(Now - ScheduledOpeningEvaluator.MissedBookingGrace.Add(TimeSpan.FromMinutes(1)), Now)
            .Should().BeTrue();
    }

    [Fact]
    public void TheTwoEdgesMeetExactlyOnce_AtTheGraceBoundary()
    {
        // The one property worth protecting: a booking is always openable or expired, never both
        // and never neither, so no room can fall into a window where the clock ignores it. The
        // boundary instant itself belongs to opening.
        var boundary = Now - ScheduledOpeningEvaluator.MissedBookingGrace;

        ScheduledOpeningEvaluator.ShouldOpen(boundary, Now).Should().BeTrue();
        ScheduledOpeningEvaluator.HasExpired(boundary, Now).Should().BeFalse();

        var justPast = boundary.AddTicks(-1);
        ScheduledOpeningEvaluator.ShouldOpen(justPast, Now).Should().BeFalse();
        ScheduledOpeningEvaluator.HasExpired(justPast, Now).Should().BeTrue();
    }

    [Fact]
    public void OpeningSweepCandidateFilter_PicksExactlyTheRoomsShouldOpenAgreesWith()
    {
        var due = Room("SCHEDULED", Now);
        var lateButInside = Room("SCHEDULED", Now.AddHours(-1));
        var notYet = Room("SCHEDULED", Now.AddMinutes(5));
        var stale = Room("SCHEDULED", Now.AddHours(-3));

        var matched = new[] { due, lateButInside, notYet, stale }
            .Where(ScheduledOpeningEvaluator.OpeningSweepCandidateFilter(Now).Compile())
            .ToList();

        matched.Should().BeEquivalentTo(new[] { due, lateButInside });
        matched.Should().OnlyContain(room => ScheduledOpeningEvaluator.ShouldOpen(room.ScheduledAt!.Value, Now));
    }

    [Theory]
    [InlineData("WAITING")]
    [InlineData("OPEN")]
    [InlineData("IN_PROGRESS")]
    [InlineData("CANCELLED")]
    [InlineData("ENDED")]
    [InlineData("EXPIRED")]
    public void OpeningSweepCandidateFilter_LeavesEveryOtherStatusAlone(string status)
    {
        // A host who opened the lobby, started early or cancelled has already decided this room's
        // fate; the clock does not get to overrule them. OPEN is in the list because re-opening an
        // open room is how a second round of notifications would go out.
        var room = Room(status, Now);

        ScheduledOpeningEvaluator.OpeningSweepCandidateFilter(Now).Compile()(room).Should().BeFalse();
    }

    [Fact]
    public void OpeningSweepCandidateFilter_SkipsDeletedRooms()
    {
        var room = Room("SCHEDULED", Now);
        room.DeletedAt = Now.AddDays(-1);

        ScheduledOpeningEvaluator.OpeningSweepCandidateFilter(Now).Compile()(room).Should().BeFalse();
    }

    [Fact]
    public void OpeningSweepCandidateFilter_SkipsARoomWithNoScheduledTime()
    {
        // An instant room is created WAITING, but nothing stops a row from having no slot — and a
        // booking with no time cannot be due.
        var room = Room("SCHEDULED", scheduledAt: null);

        ScheduledOpeningEvaluator.OpeningSweepCandidateFilter(Now).Compile()(room).Should().BeFalse();
    }

    [Fact]
    public void ExpirySweepCandidateFilter_PicksExactlyTheRoomsHasExpiredAgreesWith()
    {
        var neverOpened = Room("SCHEDULED", Now.AddHours(-3));
        var openedAndUnattended = Room("OPEN", Now.AddHours(-3));
        var stillInsideGrace = Room("SCHEDULED", Now.AddHours(-1));
        var notYet = Room("SCHEDULED", Now.AddMinutes(5));

        var matched = new[] { neverOpened, openedAndUnattended, stillInsideGrace, notYet }
            .Where(ScheduledOpeningEvaluator.ExpirySweepCandidateFilter(Now).Compile())
            .ToList();

        matched.Should().BeEquivalentTo(new[] { neverOpened, openedAndUnattended });
        matched.Should().OnlyContain(room => ScheduledOpeningEvaluator.HasExpired(room.ScheduledAt!.Value, Now));
    }

    [Theory]
    [InlineData("IN_PROGRESS")]
    [InlineData("PAUSED")]
    [InlineData("ENDED")]
    [InlineData("CANCELLED")]
    [InlineData("EXPIRED")]
    [InlineData("FAILED")]
    public void ExpirySweepCandidateFilter_LeavesARoomSomebodyActuallyUsedAlone(string status)
    {
        // A room anyone entered is IN_PROGRESS or beyond by now, which is exactly why the rule
        // needs no participant check — and why a long meeting that overran its two hours must not
        // be swept out from under the people still in it.
        var room = Room(status, Now.AddHours(-5));

        ScheduledOpeningEvaluator.ExpirySweepCandidateFilter(Now).Compile()(room).Should().BeFalse();
    }

    [Fact]
    public void ExpirySweepCandidateFilter_LeavesALobbyAlone()
    {
        // WAITING is a host sitting in the room waiting for people. Emptiness is the idle sweeps'
        // measure, not the booking clock's.
        var room = Room("WAITING", Now.AddHours(-5));

        ScheduledOpeningEvaluator.ExpirySweepCandidateFilter(Now).Compile()(room).Should().BeFalse();
    }

    [Fact]
    public void ExpirySweepCandidateFilter_SkipsDeletedRooms()
    {
        var room = Room("SCHEDULED", Now.AddHours(-5));
        room.DeletedAt = Now.AddDays(-1);

        ScheduledOpeningEvaluator.ExpirySweepCandidateFilter(Now).Compile()(room).Should().BeFalse();
    }

    [Fact]
    public void ExpirySweepCandidateFilter_SkipsARoomWithNoScheduledTime()
    {
        // No slot, no missed slot. An instant room that was never used is the idle sweeps' problem.
        var room = Room("SCHEDULED", scheduledAt: null);

        ScheduledOpeningEvaluator.ExpirySweepCandidateFilter(Now).Compile()(room).Should().BeFalse();
    }

    [Fact]
    public void NoRoomIsEverBothOpenableAndExpired()
    {
        // The drift guard in predicate form: both halves of the sweep read one grace constant, so
        // the two candidate sets must be disjoint at every offset around the boundary.
        var offsets = new[]
        {
            TimeSpan.Zero,
            TimeSpan.FromMinutes(-5),
            TimeSpan.FromMinutes(119),
            ScheduledOpeningEvaluator.MissedBookingGrace,
            ScheduledOpeningEvaluator.MissedBookingGrace.Add(TimeSpan.FromTicks(1)),
            TimeSpan.FromHours(5),
        };

        var opening = ScheduledOpeningEvaluator.OpeningSweepCandidateFilter(Now).Compile();
        var expiry = ScheduledOpeningEvaluator.ExpirySweepCandidateFilter(Now).Compile();

        foreach (var offset in offsets)
        {
            var room = Room("SCHEDULED", Now - offset);
            (opening(room) && expiry(room)).Should().BeFalse("no room may be due and missed at once (offset {0})", offset);
        }
    }

    private static TranslationRoom Room(string status, DateTime? scheduledAt) => new()
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
}
