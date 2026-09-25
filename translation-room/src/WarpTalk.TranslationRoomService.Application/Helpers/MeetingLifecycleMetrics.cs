using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Threading;

namespace WarpTalk.TranslationRoomService.Application.Helpers;

/// <summary>
/// The platform's live-meeting success rate, as counters Prometheus can take a rate of.
///
/// Before this, nothing in the platform could say how many meetings worked. Every service answered
/// its own health check with a 200 while meetings produced no captions (WT-402), and the admin
/// System Health screen could only report exporters and stream lag — never the outcome a customer
/// actually sees. These are that outcome.
///
/// EXPORTED AS (OTel collector, namespace "warptalk", job label <c>exported_job</c>):
///   warptalk_meeting_started_total                              first person joined a room
///   warptalk_meeting_ended_total{end_reason, reached_live}      the room ended, and how
///   warptalk_meeting_duration_seconds_bucket{reached_live}      first join → end
///   warptalk_meeting_live_rooms / warptalk_meeting_occupied_rooms  sweep snapshot (see below)
///
/// "REACHED LIVE" is decided at the end, from durable facts, not guessed at the start: at least
/// two people joined AND the Gateway delivered at least one caption to the room
/// (<see cref="WarpTalk.Shared.MeetingLifecycleKeys"/>). A meeting that ended without both is a
/// failure whatever else happened in it — the product is translation between people, and that did
/// not happen.
///
/// The meter name is the service name, which is what <c>AddWarpTalkObservability</c> subscribes
/// to; an instrument on any other meter is created, incremented and exported nowhere.
/// </summary>
public static class MeetingLifecycleMetrics
{
    public const string MeterName = "warptalk-translation-room";

    public const string EndReasonHost = "host";
    public const string EndReasonAbandoned = "abandoned";
    public const string EndReasonExpired = "expired";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> Started = Meter.CreateCounter<long>(
        "meeting.started",
        description: "Rooms that received their first successful join.");

    private static readonly Counter<long> Ended = Meter.CreateCounter<long>(
        "meeting.ended",
        description: "Rooms that ended, by who ended them and whether they ever reached live.");

    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>(
        "meeting.duration",
        unit: "s",
        description: "First join to end, per meeting.");

    /// <summary>
    /// Which caller is ending a room. EndTranslationRoomAsync is the one path every ending takes
    /// — the host's button and the abandoned-room sweep both call it with the room's host id — so
    /// its arguments cannot say who asked. An ambient value can, without widening a signature
    /// that four services and a test suite of mocks are built against.
    /// </summary>
    private static readonly AsyncLocal<string?> EndReason = new();

    private static long _liveRooms;
    private static long _occupiedRooms;
    private static long _snapshotUnixMs;

    /// <summary>A snapshot older than this is withheld rather than reported (see <see cref="RecordRoomSnapshot"/>).</summary>
    internal static readonly TimeSpan SnapshotFreshness = TimeSpan.FromMinutes(12);

    static MeetingLifecycleMetrics()
    {
        Meter.CreateObservableGauge(
            "meeting.live_rooms",
            () => FreshSnapshot(Interlocked.Read(ref _liveRooms)),
            description: "Rooms in a live status, as of the last abandoned-room sweep.");
        Meter.CreateObservableGauge(
            "meeting.occupied_rooms",
            () => FreshSnapshot(Interlocked.Read(ref _occupiedRooms)),
            description: "Live rooms with at least one person in them, as of the last sweep.");
    }

    /// <summary>The end reason in force for the current async flow; <see cref="EndReasonHost"/> by default.</summary>
    public static string CurrentEndReason => EndReason.Value ?? EndReasonHost;

    /// <summary>
    /// Marks every room ended inside the returned scope with <paramref name="reason"/>. Disposing
    /// restores whatever was in force before, so scopes nest.
    /// </summary>
    public static IDisposable EndReasonScope(string reason)
    {
        var previous = EndReason.Value;
        EndReason.Value = reason;
        return new Restore(() => EndReason.Value = previous);
    }

    public static void RecordStarted() => Started.Add(1);

    public static void RecordEnded(string endReason, bool reachedLive, TimeSpan? duration)
    {
        var live = reachedLive ? "true" : "false";
        Ended.Add(
            1,
            new KeyValuePair<string, object?>("end_reason", endReason),
            new KeyValuePair<string, object?>("reached_live", live));

        if (duration is { } value && value >= TimeSpan.Zero)
        {
            Duration.Record(
                value.TotalSeconds,
                new KeyValuePair<string, object?>("reached_live", live));
        }
    }

    /// <summary>
    /// The sweep's view of the platform, published as two gauges.
    ///
    /// The sweep runs every five minutes under a distributed lock, so exactly one replica holds a
    /// current value and the others hold whatever they saw the last time they won the lock. An
    /// observable gauge with no freshness rule would have every former lock holder keep reporting
    /// its old count for ever; withholding a stale snapshot is what makes <c>max()</c> across
    /// replicas the right aggregation on the dashboard.
    /// </summary>
    public static void RecordRoomSnapshot(int liveRooms, int occupiedRooms, DateTimeOffset observedAt)
    {
        Interlocked.Exchange(ref _liveRooms, liveRooms);
        Interlocked.Exchange(ref _occupiedRooms, occupiedRooms);
        Interlocked.Exchange(ref _snapshotUnixMs, observedAt.ToUnixTimeMilliseconds());
    }

    /// <summary>
    /// Whether a meeting counts as having reached live: two people and a caption.
    /// </summary>
    public static bool ReachedLive(int everJoined, bool captionDelivered) => everJoined >= 2 && captionDelivered;

    private static IEnumerable<Measurement<long>> FreshSnapshot(long value)
    {
        var at = Interlocked.Read(ref _snapshotUnixMs);
        if (at == 0) yield break;
        var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(at);
        if (age > SnapshotFreshness) yield break;
        yield return new Measurement<long>(value);
    }

    private sealed class Restore(Action restore) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) restore();
        }
    }
}
