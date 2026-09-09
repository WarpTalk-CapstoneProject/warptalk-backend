using System;
using System.Collections.Generic;
using FluentAssertions;
using WarpTalk.TranslationRoomService.Application.Helpers;
using WarpTalk.TranslationRoomService.Domain.Entities;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Application.Helpers;

/// <summary>
/// How many times a meeting that ended without a summary is worth asking again.
///
/// The sweep that uses this exists because finalization can be lost entirely — the queue it
/// travels on is an in-memory channel, so a restart drops it, and the worker that drains it
/// swallows every exception. So retrying has to be real. But a room whose finalization fails
/// permanently is re-queued every five minutes forever unless something counts, which is what
/// this decides.
///
/// The third state is the one worth pinning. Two states — under the limit and over it — force a
/// choice between going silent the moment you give up (leaving no record that a meeting's
/// summary is never coming) and logging the same warning every five minutes until the TTL
/// expires. Separating the crossing from everything after it says it exactly once.
/// </summary>
public class ArtifactsReconciliationPolicyTests
{
    private const int MaxAttempts = 5;

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    public void AnAttemptWithinTheLimitIsRetried(int attempts)
    {
        ArtifactsReconciliationPolicy
            .Decide(attempts, MaxAttempts)
            .Should()
            .Be(ReconciliationAction.Requeue);
    }

    [Fact]
    public void TheAttemptThatCrossesTheLimitIsTheOneThatSaysSo()
    {
        ArtifactsReconciliationPolicy
            .Decide(MaxAttempts + 1, MaxAttempts)
            .Should()
            .Be(ReconciliationAction.AbandonAndWarn);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(50)]
    [InlineData(4_000)]
    public void EverySweepAfterThatIsSilent(int attempts)
    {
        // The counter keeps incrementing for as long as the room keeps matching the query, and
        // a sweep runs every five minutes. Warning on each one would bury the log in a message
        // whose news value expired the first time it was printed.
        ArtifactsReconciliationPolicy
            .Decide(attempts, MaxAttempts)
            .Should()
            .Be(ReconciliationAction.Skip);
    }

    [Fact]
    public void TheGivingUpIsAnnouncedExactlyOnce()
    {
        var announcements = 0;
        for (var attempt = 1; attempt <= 100; attempt++)
        {
            if (ArtifactsReconciliationPolicy.Decide(attempt, MaxAttempts)
                == ReconciliationAction.AbandonAndWarn)
            {
                announcements++;
            }
        }

        announcements.Should().Be(1);
    }

    [Fact]
    public void AZeroLimitStillRetriesNothingRatherThanEverything()
    {
        // A misconfigured MaxRecoverySweeps of 0 must mean "do not retry", not "retry forever".
        ArtifactsReconciliationPolicy.Decide(1, 0).Should().Be(ReconciliationAction.AbandonAndWarn);
        ArtifactsReconciliationPolicy.Decide(2, 0).Should().Be(ReconciliationAction.Skip);
    }
}

/// <summary>
/// WHICH ROOMS A SWEEP IS ALLOWED TO FINALIZE.
///
/// Both sweeps selected on TerminalStatuses — ENDED, CANCELLED and EXPIRED. CancelTranslationRoomAsync
/// sets EndedAt, so a CANCELLED meeting matched "ended with no artifacts" exactly, and the sweep
/// finalized it: a transcript artifact and a summary artifact written for a conversation that never
/// happened, followed by ArtifactsFinalizationWorker announcing "Summary ready" to everyone who had
/// been invited. The people who received that notification had cancelled the meeting themselves.
///
/// These tests compile the SAME expression the worker hands to EF, so what is proved here is what
/// the database is asked — the reason the predicate moved into the policy rather than being copied
/// into a test as a second, drift-prone rule.
/// </summary>
public class ArtifactsReconciliationScopeTests
{
    private static readonly DateTime Now = new(2026, 9, 9, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime QueuedBefore = Now.AddMinutes(-10);
    private static readonly DateTime EndedAfter = Now.AddHours(-24);

    private static TranslationRoom Room(string status, DateTime? endedAt, bool withArtifact = false) =>
        new()
        {
            Id = Guid.NewGuid(),
            Status = status,
            EndedAt = endedAt,
            TranslationRoomArtifacts = withArtifact
                ? new List<TranslationRoomArtifact> { new() { Id = Guid.NewGuid() } }
                : new List<TranslationRoomArtifact>(),
        };

    [Theory]
    [InlineData("CANCELLED")]
    [InlineData("EXPIRED")]
    public void AMeetingThatNeverHappenedIsNotFinalized(string status)
    {
        var matches = ArtifactsReconciliationPolicy
            .AbandonedRooms(QueuedBefore, EndedAfter)
            .Compile();

        matches(Room(status, Now.AddHours(-1))).Should().BeFalse();
    }

    [Fact]
    public void AMeetingThatEndedWithNothingToShowForItIsFinalized()
    {
        var matches = ArtifactsReconciliationPolicy
            .AbandonedRooms(QueuedBefore, EndedAfter)
            .Compile();

        matches(Room("ENDED", Now.AddHours(-1))).Should().BeTrue();
    }

    [Fact]
    public void AMeetingInsideTheGracePeriodIsLeftAlone()
    {
        // Finalization is probably still running. Re-queueing it here is how a room gets two
        // transcripts.
        var matches = ArtifactsReconciliationPolicy
            .AbandonedRooms(QueuedBefore, EndedAfter)
            .Compile();

        matches(Room("ENDED", Now.AddMinutes(-1))).Should().BeFalse();
    }

    [Fact]
    public void AMeetingThatAlreadyHasArtifactsIsNotSweptAgain()
    {
        var matches = ArtifactsReconciliationPolicy
            .AbandonedRooms(QueuedBefore, EndedAfter)
            .Compile();

        matches(Room("ENDED", Now.AddHours(-1), withArtifact: true)).Should().BeFalse();
    }

    [Fact]
    public void ARoomWithNoEndedAtIsInvisibleToTheSweep()
    {
        var matches = ArtifactsReconciliationPolicy
            .AbandonedRooms(QueuedBefore, EndedAfter)
            .Compile();

        matches(Room("ENDED", null)).Should().BeFalse();
    }

    [Theory]
    [InlineData("CANCELLED")]
    [InlineData("EXPIRED")]
    public void LateSummaryRecoveryAlsoIgnoresAMeetingThatNeverHappened(string status)
    {
        var matches = ArtifactsReconciliationPolicy
            .RecentlyEndedWithArtifacts(EndedAfter)
            .Compile();

        matches(Room(status, Now.AddHours(-1), withArtifact: true)).Should().BeFalse();
        matches(Room("ENDED", Now.AddHours(-1), withArtifact: true)).Should().BeTrue();
    }
}
