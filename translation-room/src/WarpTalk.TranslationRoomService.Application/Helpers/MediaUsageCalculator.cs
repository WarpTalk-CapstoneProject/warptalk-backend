using System;
using System.Collections.Generic;
using System.Linq;
using WarpTalk.TranslationRoomService.Domain.Interfaces;

namespace WarpTalk.TranslationRoomService.Application.Helpers;

/// <summary>
/// LiveKit usage per UTC hour and workspace, for the admin Providers page (billing-service reads
/// it over the GetMediaUsage RPC). Pure: the clock is a parameter.
///
/// <para><b>Room seconds</b>: meeting wall-clock time inside the hour, with the end of a meeting
/// resolved exactly as <see cref="AdminMeetingInsightsCalculator.ResolveEnd"/> does (live rooms run
/// to now, capped at 24 h; a finished room with no end uses duration_seconds; otherwise left out),
/// so Providers and Insights agree on how long meetings ran.</para>
///
/// <para><b>Participant seconds</b>: each participant's LAST join → leave, clipped to its room's
/// span and to the hour. A participant with no leave ends with the room. This is an estimate of
/// what LiveKit bills as participant minutes: a rejoin overwrites the earlier stay (undercount),
/// and the dubbing and ingress bots that also hold a LiveKit connection have no participant row
/// (undercount). It is not reconciled against a LiveKit invoice.</para>
///
/// <para><b>Recordings</b>: egress recordings created in the hour, and their size where known.</para>
/// </summary>
public static class MediaUsageCalculator
{
    public sealed record HourRow(
        DateTime HourStart,
        Guid WorkspaceId,
        double RoomSeconds,
        double ParticipantSeconds,
        int RoomsStarted,
        int Recordings,
        long RecordingBytes);

    public static IReadOnlyList<HourRow> Hourly(
        IReadOnlyList<MediaUsageRoomSpan> rooms,
        IReadOnlyList<MediaUsageParticipantSpan> participants,
        IReadOnlyList<MediaUsageRecording> recordings,
        DateTime from,
        DateTime to,
        DateTime now)
    {
        var sums = new Dictionary<(DateTime Hour, Guid Workspace), Sum>();
        Sum At(DateTime hour, Guid workspace)
        {
            if (!sums.TryGetValue((hour, workspace), out var sum)) sums[(hour, workspace)] = sum = new Sum();
            return sum;
        }

        var spans = new Dictionary<Guid, (Guid Workspace, DateTime Start, DateTime End)>();
        foreach (var room in rooms)
        {
            if (room.StartedAt >= from && room.StartedAt < to) At(HourOf(room.StartedAt), room.WorkspaceId).RoomsStarted++;

            var (end, _) = AdminMeetingInsightsCalculator.ResolveEnd(room.StartedAt, room.EndedAt, room.DurationSeconds, room.Status, now);
            if (end is null || end.Value <= room.StartedAt) continue;
            spans[room.RoomId] = (room.WorkspaceId, room.StartedAt, end.Value);
            Spread(room.StartedAt, end.Value, from, to, (hour, seconds) => At(hour, room.WorkspaceId).RoomSeconds += seconds);
        }

        foreach (var participant in participants)
        {
            if (!spans.TryGetValue(participant.RoomId, out var span)) continue;
            var start = participant.JoinedAt > span.Start ? participant.JoinedAt : span.Start;
            var end = participant.LeftAt is { } left && left < span.End ? left : span.End;
            if (end <= start) continue;
            Spread(start, end, from, to, (hour, seconds) => At(hour, span.Workspace).ParticipantSeconds += seconds);
        }

        foreach (var recording in recordings)
        {
            if (recording.CreatedAt < from || recording.CreatedAt >= to) continue;
            var sum = At(HourOf(recording.CreatedAt), recording.WorkspaceId);
            sum.Recordings++;
            sum.RecordingBytes += Math.Max(0, recording.SizeBytes ?? 0);
        }

        return sums
            .Select(pair => new HourRow(
                pair.Key.Hour,
                pair.Key.Workspace,
                Math.Round(pair.Value.RoomSeconds, 1),
                Math.Round(pair.Value.ParticipantSeconds, 1),
                pair.Value.RoomsStarted,
                pair.Value.Recordings,
                pair.Value.RecordingBytes))
            .Where(row => row.RoomSeconds > 0 || row.ParticipantSeconds > 0 || row.RoomsStarted > 0 || row.Recordings > 0)
            .OrderBy(row => row.HourStart)
            .ThenBy(row => row.WorkspaceId)
            .ToList();
    }

    public static DateTime HourOf(DateTime at)
    {
        var utc = at.Kind == DateTimeKind.Local ? at.ToUniversalTime() : at;
        return new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, DateTimeKind.Utc);
    }

    /// <summary>Calls <paramref name="add"/> with each UTC hour of [start, end) ∩ [from, to) and the seconds in it.</summary>
    private static void Spread(DateTime start, DateTime end, DateTime from, DateTime to, Action<DateTime, double> add)
    {
        var s = start > from ? start : from;
        var e = end < to ? end : to;
        if (e <= s) return;

        var hour = HourOf(s);
        while (hour < e)
        {
            var next = hour.AddHours(1);
            var a = s > hour ? s : hour;
            var b = e < next ? e : next;
            if (b > a) add(hour, (b - a).TotalSeconds);
            hour = next;
        }
    }

    private sealed class Sum
    {
        public double RoomSeconds;
        public double ParticipantSeconds;
        public int RoomsStarted;
        public int Recordings;
        public long RecordingBytes;
    }
}
