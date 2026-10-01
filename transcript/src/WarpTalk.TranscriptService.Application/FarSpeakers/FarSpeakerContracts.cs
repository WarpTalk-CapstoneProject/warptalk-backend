using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.TranscriptService.Domain.Entities;

namespace WarpTalk.TranscriptService.Application.FarSpeakers;

/// <summary>What the relabel job needs to know about a room, from TranslationRoomService.</summary>
/// <param name="HostUserId">The effective host — whose Google grant is used.</param>
/// <param name="EndedAt">Null while the room is still open.</param>
public sealed record BridgeRoomInfo(
    Guid RoomId,
    bool IsBridge,
    string? ExternalMeetingUrl,
    Guid? HostUserId,
    Guid? BookerUserId,
    string Status,
    DateTime? StartedAt,
    DateTime? EndedAt);

public enum BridgeRoomLookupOutcome
{
    Found,
    NotFound,
    Unavailable,
}

public interface IBridgeRoomLookup
{
    Task<(BridgeRoomLookupOutcome Outcome, BridgeRoomInfo? Room)> GetAsync(Guid roomId, CancellationToken ct = default);
}

/// <summary>Google Meet's transcript for the conferences overlapping a window, via AssistantService.</summary>
public sealed record MeetTranscriptFetch(
    string? ErrorCode,
    string? Error,
    int ConferenceRecords,
    int TranscriptsReady,
    int TranscriptsPending,
    bool ConferenceLive,
    IReadOnlyList<MeetTranscriptLine> Entries);

public interface IMeetTranscriptSource
{
    Task<MeetTranscriptFetch> GetEntriesAsync(
        Guid hostUserId,
        string meeting,
        DateTime windowStartUtc,
        DateTime windowEndUtc,
        CancellationToken ct = default);
}

/// <summary>The current transcript of a room and its stand-in segments (tracked, so they can be updated).</summary>
public sealed record StandInTranscript(Guid TranscriptId, DateTime? TimelineAnchorAt, IReadOnlyList<TranscriptSegment> Segments);

public interface IFarSpeakerRelabelStore
{
    /// <summary>
    /// Rooms that have stand-in segments written since <paramref name="since"/> and no job row yet.
    /// </summary>
    Task<IReadOnlyList<Guid>> FindUnqueuedBridgeRoomsAsync(Guid standInId, DateTime since, int limit, CancellationToken ct = default);

    Task AddJobsAsync(IEnumerable<FarSpeakerRelabelJob> jobs, CancellationToken ct = default);

    /// <summary>Pending jobs whose next attempt is due, oldest first. Tracked.</summary>
    Task<IReadOnlyList<FarSpeakerRelabelJob>> GetDueJobsAsync(DateTime now, int limit, CancellationToken ct = default);

    /// <summary>The room's current transcript and its stand-in segments, or null when it has none.</summary>
    Task<StandInTranscript?> GetStandInTranscriptAsync(Guid roomId, Guid standInId, CancellationToken ct = default);

    Task SaveChangesAsync(CancellationToken ct = default);
}

public interface IFarSpeakerRelabelService
{
    /// <summary>Queues a job for every bridge room that has stand-in segments and none yet.</summary>
    Task<int> DiscoverAsync(CancellationToken ct = default);

    /// <summary>Runs every due job once. Returns how many were attempted.</summary>
    Task<int> ProcessDueAsync(CancellationToken ct = default);
}
