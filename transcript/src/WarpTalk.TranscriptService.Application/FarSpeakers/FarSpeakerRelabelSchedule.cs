using System;

namespace WarpTalk.TranscriptService.Application.FarSpeakers;

/// <summary>
/// When the relabel job tries again and when it stops. Pure, so the timings are testable.
///
/// Google writes the transcript entries some minutes after the conference ends and deletes them
/// after 30 days, so: retry with doubling backoff from <see cref="FirstRetry"/> capped at
/// <see cref="MaxBackoff"/>, and give up <see cref="GiveUpAfter"/> after the room ended. A meeting
/// that never had Meet transcription turned on would otherwise poll for a month; once
/// <see cref="NoTranscriptAfter"/> has passed with conference records but no transcript at all, the
/// job concludes there is none.
/// </summary>
public static class FarSpeakerRelabelSchedule
{
    public static readonly TimeSpan FirstRetry = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(2);
    public static readonly TimeSpan GiveUpAfter = TimeSpan.FromDays(30);
    public static readonly TimeSpan NoTranscriptAfter = TimeSpan.FromHours(24);

    /// <summary>How often a room that has not ended yet is looked at again. Not counted as an attempt.</summary>
    public static readonly TimeSpan RoomStillOpenRecheck = TimeSpan.FromMinutes(10);

    /// <summary>The wait after the <paramref name="attempts"/>-th failed attempt (1-based).</summary>
    public static TimeSpan Backoff(int attempts)
    {
        if (attempts <= 1) return FirstRetry;

        // 2, 4, 8, ... minutes; the exponent is clamped long before it could overflow.
        var exponent = Math.Min(attempts - 1, 16);
        var minutes = FirstRetry.TotalMinutes * Math.Pow(2, exponent);
        return minutes >= MaxBackoff.TotalMinutes ? MaxBackoff : TimeSpan.FromMinutes(minutes);
    }

    /// <summary>True once the transcript Google keeps for 30 days can no longer be there.</summary>
    public static bool ShouldGiveUp(DateTime roomEndedAtUtc, DateTime nowUtc) =>
        nowUtc - roomEndedAtUtc >= GiveUpAfter;

    /// <summary>True once a conference with no transcript at all has had long enough to grow one.</summary>
    public static bool ShouldConcludeNoTranscript(DateTime roomEndedAtUtc, DateTime nowUtc) =>
        nowUtc - roomEndedAtUtc >= NoTranscriptAfter;
}
