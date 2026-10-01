namespace WarpTalk.TranslationRoomService.Application.Helpers;

/// <summary>One Google Meet conference held under a bridge room's Meet code. EndTime null = live.</summary>
public sealed record MeetConferenceRecordInfo(string Name, DateTime? StartTime, DateTime? EndTime);

public enum MeetConferenceEndVerdict
{
    /// <summary>Google has no conference for the code yet: nobody has joined the Meet. Keep the room.</summary>
    NoConference,

    /// <summary>A conference for the code is in progress. Keep the room.</summary>
    ConferenceLive,

    /// <summary>
    /// The latest conference ended before this room started — a previous meeting on the same code
    /// (recurring events reuse Meet codes). This room's conference has not begun. Keep the room.
    /// </summary>
    EndedBeforeRoom,

    /// <summary>The Meet this room bridges is over. End the room.</summary>
    End,
}

/// <summary>
/// Whether a Google Meet bridge room should end because the Google Meet conference it bridges has
/// ended. The popup has no End button: the meeting ends when the Meet does.
///
/// Pure — the worker that asks Google and ends rooms is <c>MeetConferenceEndWorker</c>.
/// </summary>
public static class MeetConferenceEndPolicy
{
    /// <param name="records">Every conference record Google lists for the room's Meet code.</param>
    /// <param name="roomCreatedAt">The room's creation time (UTC).</param>
    /// <param name="roomStartedAt">When the room started (UTC), if it has.</param>
    public static MeetConferenceEndVerdict Decide(
        IReadOnlyList<MeetConferenceRecordInfo> records,
        DateTime roomCreatedAt,
        DateTime? roomStartedAt)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0)
            return MeetConferenceEndVerdict.NoConference;

        // Any live record means the Meet is on, whatever older records say. A record with no end
        // time is live by Google's definition.
        if (records.Any(r => r.EndTime is null))
            return MeetConferenceEndVerdict.ConferenceLive;

        var latestEnd = records.Max(r => r.EndTime!.Value);

        // The room's own life started at its start (or, before it started, at its creation). A
        // conference that was already over by then belongs to an earlier meeting on the same code,
        // and ending this room for it would kill a meeting that has not had its Meet yet.
        var roomBegan = roomStartedAt ?? roomCreatedAt;
        if (latestEnd <= roomBegan)
            return MeetConferenceEndVerdict.EndedBeforeRoom;

        return MeetConferenceEndVerdict.End;
    }
}
