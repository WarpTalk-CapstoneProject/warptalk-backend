using System;
using FluentAssertions;
using WarpTalk.TranslationRoomService.Application.Helpers;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Application.Helpers;

/// <summary>
/// When a meeting nobody is in should be ended.
///
/// Nothing ended a room when the last person left, so production accumulated rooms reporting
/// LIVE NOW since 9 August — never reaching History, still claiming occupancy, and with their
/// transcript and summary never finalized, because finalization is queued by ending.
///
/// The dangerous direction is the other one. This decides to end a meeting, and a meeting ended
/// out from under the people in it is far worse than one that lingers, so the cases pinned here
/// are mostly about NOT ending: the reconnect gap, the host waiting alone, and the single bad
/// count that must never be enough on its own.
/// </summary>
public class AbandonedRoomPolicyTests
{
    private static readonly DateTime Now = new(2026, 8, 13, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ARoomWithSomebodyInItIsLeftAlone()
    {
        AbandonedRoomPolicy
            .Decide(seatHolders: 1, emptySince: Now.AddDays(-3), now: Now)
            .Should()
            .Be(AbandonedRoomAction.Leave);
    }

    [Fact]
    public void AnOldObservationCannotEndARoomThatRefilled()
    {
        // The caller clears the timestamp on this branch. Without that, a room empty overnight,
        // rejoined at 9am, would be ended on the first sweep after the meeting started.
        AbandonedRoomPolicy
            .Decide(seatHolders: 4, emptySince: Now.AddHours(-9), now: Now)
            .Should()
            .Be(AbandonedRoomAction.Leave);
    }

    [Fact]
    public void TheFirstSweepThatFindsARoomEmptyOnlyWritesItDown()
    {
        // One observation is never enough. A database blip or a roster mid-write reads as zero,
        // and ending a live meeting on a single bad count is the failure this ordering prevents.
        AbandonedRoomPolicy
            .Decide(seatHolders: 0, emptySince: null, now: Now)
            .Should()
            .Be(AbandonedRoomAction.StartGrace);
    }

    [Fact]
    public void ARoomEmptiedMomentsAgoSurvives()
    {
        // Somebody dropped off wifi. They get the whole grace period to come back.
        AbandonedRoomPolicy
            .Decide(seatHolders: 0, emptySince: Now.AddSeconds(-30), now: Now)
            .Should()
            .Be(AbandonedRoomAction.Leave);
    }

    [Fact]
    public void AHostWaitingAloneForTheGracePeriodIsNotEndedEarly()
    {
        // Opening the room early and waiting is ordinary. At exactly the grace period it has not
        // been empty FOR longer than the grace, and the next sweep is minutes away.
        AbandonedRoomPolicy
            .Decide(seatHolders: 0, emptySince: Now - AbandonedRoomPolicy.GracePeriod, now: Now)
            .Should()
            .Be(AbandonedRoomAction.Leave);
    }

    [Fact]
    public void ARoomEmptyForLongerThanTheGraceIsEnded()
    {
        AbandonedRoomPolicy
            .Decide(
                seatHolders: 0,
                emptySince: Now - AbandonedRoomPolicy.GracePeriod - TimeSpan.FromSeconds(1),
                now: Now)
            .Should()
            .Be(AbandonedRoomAction.End);
    }

    [Fact]
    public void TheRoomsStuckSinceAugustAreEnded()
    {
        // The actual report: meetings started on 9 August still showing LIVE NOW on the 13th.
        AbandonedRoomPolicy
            .Decide(seatHolders: 0, emptySince: Now.AddDays(-4), now: Now)
            .Should()
            .Be(AbandonedRoomAction.End);
    }

    [Fact]
    public void TheGraceIsLongEnoughToBeWorthHaving()
    {
        // Pinned as a property rather than a value: a grace of seconds would end meetings during
        // an ordinary reconnect, and one of days would leave the rooms this exists to clean up.
        AbandonedRoomPolicy.GracePeriod.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMinutes(10));
        AbandonedRoomPolicy.GracePeriod.Should().BeLessThanOrEqualTo(TimeSpan.FromHours(2));
    }
}
