using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.Shared;
using WarpTalk.TranscriptService.Domain;
using WarpTalk.TranscriptService.Domain.Entities;

namespace WarpTalk.TranscriptService.Application.FarSpeakers;

/// <summary>
/// After a Google Meet bridge room ends, names its far-side segments from Google Meet's own
/// speaker-attributed transcript.
///
/// Only segments whose speaker is the bridge stand-in are ever touched — a real WarpTalk
/// participant's segment is attributed by identity already — and of those, never one a host has
/// labelled (<see cref="FarSpeakerSources.Host"/>). A segment the alignment cannot attribute with
/// enough overlap keeps the name it has.
/// </summary>
public sealed class FarSpeakerRelabelService : IFarSpeakerRelabelService
{
    /// <summary>Matches transcript_segments.speaker_name's varchar(100).</summary>
    internal const int SpeakerNameMaxLength = 100;

    /// <summary>Slack around the segments' own span when choosing which conference records to read.</summary>
    internal static readonly TimeSpan WindowSlack = TimeSpan.FromMinutes(10);

    private const int DiscoverBatch = 50;
    private const int ProcessBatch = 20;

    private readonly IFarSpeakerRelabelStore _store;
    private readonly IBridgeRoomLookup _rooms;
    private readonly IMeetTranscriptSource _meet;
    private readonly TimeProvider _time;
    private readonly ILogger<FarSpeakerRelabelService> _logger;

    public FarSpeakerRelabelService(
        IFarSpeakerRelabelStore store,
        IBridgeRoomLookup rooms,
        IMeetTranscriptSource meet,
        ILogger<FarSpeakerRelabelService> logger,
        TimeProvider? time = null)
    {
        _store = store;
        _rooms = rooms;
        _meet = meet;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public async Task<int> DiscoverAsync(CancellationToken ct = default)
    {
        var now = Now();
        var roomIds = await _store.FindUnqueuedBridgeRoomsAsync(
            ExternalBridgeConstants.ParticipantUserId,
            now - FarSpeakerRelabelSchedule.GiveUpAfter,
            DiscoverBatch,
            ct);
        if (roomIds.Count == 0) return 0;

        await _store.AddJobsAsync(roomIds.Select(id => new FarSpeakerRelabelJob
        {
            TranslationRoomId = id,
            Status = FarSpeakerRelabelJobStatuses.Pending,
            NextAttemptAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        }), ct);
        await _store.SaveChangesAsync(ct);
        return roomIds.Count;
    }

    public async Task<int> ProcessDueAsync(CancellationToken ct = default)
    {
        var due = await _store.GetDueJobsAsync(Now(), ProcessBatch, ct);
        foreach (var job in due)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await ProcessAsync(job, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Far-speaker relabel failed for room {RoomId}", job.TranslationRoomId);
                Retry(job, Now(), "unexpected: " + ex.GetType().Name);
            }

            await _store.SaveChangesAsync(ct);
        }

        return due.Count;
    }

    internal async Task ProcessAsync(FarSpeakerRelabelJob job, CancellationToken ct)
    {
        var now = Now();
        var (outcome, room) = await _rooms.GetAsync(job.TranslationRoomId, ct);
        switch (outcome)
        {
            case BridgeRoomLookupOutcome.NotFound:
                Finish(job, FarSpeakerRelabelJobStatuses.Skipped, now, "room not found");
                return;
            case BridgeRoomLookupOutcome.Unavailable:
                Retry(job, now, "translation-room unavailable");
                return;
        }

        if (room is null || !room.IsBridge || string.IsNullOrWhiteSpace(room.ExternalMeetingUrl))
        {
            Finish(job, FarSpeakerRelabelJobStatuses.Skipped, now, "not a Google Meet bridge room");
            return;
        }

        if (room.EndedAt is null)
        {
            // Still open: not a failure, so it does not grow the backoff. A room that never ends
            // is the other reapers' problem; the job just keeps looking at it slowly.
            job.NextAttemptAt = now + FarSpeakerRelabelSchedule.RoomStillOpenRecheck;
            job.UpdatedAt = now;
            return;
        }

        var endedAt = room.EndedAt.Value;
        job.RoomEndedAt = endedAt;
        if (FarSpeakerRelabelSchedule.ShouldGiveUp(endedAt, now))
        {
            Finish(job, FarSpeakerRelabelJobStatuses.Abandoned, now, job.LastError ?? "transcript retention passed");
            return;
        }

        var host = room.HostUserId ?? room.BookerUserId;
        if (host is null)
        {
            Finish(job, FarSpeakerRelabelJobStatuses.Skipped, now, "room has no host");
            return;
        }

        var transcript = await _store.GetStandInTranscriptAsync(job.TranslationRoomId, ExternalBridgeConstants.ParticipantUserId, ct);
        if (transcript is null || transcript.Segments.Count == 0)
        {
            Finish(job, FarSpeakerRelabelJobStatuses.Skipped, now, "no stand-in segments");
            return;
        }

        if (transcript.TimelineAnchorAt is null)
        {
            // WT-473: no anchor means the segments' offsets cannot be put on a wall clock, and
            // aligning them against Google's timestamps would be a guess.
            Finish(job, FarSpeakerRelabelJobStatuses.Skipped, now, "transcript has no timeline anchor");
            return;
        }

        var anchor = DateTime.SpecifyKind(transcript.TimelineAnchorAt.Value, DateTimeKind.Utc);
        var anchorMs = new DateTimeOffset(anchor).ToUnixTimeMilliseconds();
        var windowStart = anchor.AddMilliseconds(transcript.Segments.Min(s => s.StartTimeMs)) - WindowSlack;
        var windowEnd = anchor.AddMilliseconds(transcript.Segments.Max(s => s.EndTimeMs)) + WindowSlack;

        var fetch = await _meet.GetEntriesAsync(host.Value, room.ExternalMeetingUrl!, windowStart, windowEnd, ct);
        if (!string.IsNullOrEmpty(fetch.ErrorCode))
        {
            if (fetch.ErrorCode == MeetConferenceErrorCodes.InvalidMeeting)
            {
                Finish(job, FarSpeakerRelabelJobStatuses.Skipped, now, fetch.ErrorCode);
                return;
            }

            // meet_scope_missing / connection_required: the host may still grant it — the entries
            // stay readable for 30 days. Anything else is transient.
            Retry(job, now, fetch.ErrorCode);
            return;
        }

        if (fetch.Entries.Count == 0)
        {
            if (fetch.TranscriptsPending > 0 || fetch.ConferenceLive)
            {
                Retry(job, now, "transcript not ready");
                return;
            }

            if (fetch.TranscriptsReady > 0)
            {
                // A transcript exists and is empty: nothing to attribute, and nothing will change.
                Complete(job, now, 0);
                return;
            }

            if (FarSpeakerRelabelSchedule.ShouldConcludeNoTranscript(endedAt, now))
            {
                Finish(job, FarSpeakerRelabelJobStatuses.NoTranscript, now,
                    fetch.ConferenceRecords == 0 ? "no conference record" : "Meet transcription was not on");
                return;
            }

            Retry(job, now, fetch.ConferenceRecords == 0 ? "no conference record yet" : "no transcript yet");
            return;
        }

        var relabeled = Apply(transcript.Segments, fetch.Entries, anchorMs, now);
        _logger.LogInformation(
            "Far-speaker relabel for room {RoomId}: {Relabeled} of {Total} stand-in segments named from {Entries} Meet transcript entries",
            job.TranslationRoomId, relabeled, transcript.Segments.Count, fetch.Entries.Count);
        Complete(job, now, relabeled);
    }

    /// <summary>
    /// Aligns and writes. Returns how many segments changed. Internal for the tests.
    /// </summary>
    internal static int Apply(
        IReadOnlyList<TranscriptSegment> segments,
        IReadOnlyList<MeetTranscriptLine> entries,
        long anchorMs,
        DateTime now)
    {
        var eligible = segments
            .Where(s => s.SpeakerParticipantId == ExternalBridgeConstants.ParticipantUserId)
            .Where(s => s.FarSpeakerSource != FarSpeakerSources.Host)
            .ToList();
        if (eligible.Count == 0) return 0;

        var alignment = FarSpeakerAlignment.Align(
            eligible.Select(s => new StandInSegment(s.Id, anchorMs + s.StartTimeMs, anchorMs + s.EndTimeMs, s.OriginalText)).ToList(),
            entries);

        var byId = eligible.ToDictionary(s => s.Id);
        var changed = 0;
        foreach (var assignment in alignment.Assignments)
        {
            if (!byId.TryGetValue(assignment.SegmentId, out var segment)) continue;

            var name = string.IsNullOrWhiteSpace(assignment.DisplayName)
                ? segment.SpeakerName
                : Truncate(assignment.DisplayName.Trim(), SpeakerNameMaxLength);

            if (segment.SpeakerName == name
                && segment.FarSpeakerKey == assignment.ParticipantKey
                && segment.FarSpeakerSource == FarSpeakerSources.GoogleTranscript
                && segment.FarSpeakerConfidence == assignment.Confidence)
            {
                continue;
            }

            segment.SpeakerName = name;
            segment.FarSpeakerKey = assignment.ParticipantKey;
            segment.FarSpeakerSource = FarSpeakerSources.GoogleTranscript;
            segment.FarSpeakerConfidence = assignment.Confidence;
            segment.UpdatedAt = now;
            changed++;
        }

        return changed;
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    private static void Retry(FarSpeakerRelabelJob job, DateTime now, string reason)
    {
        job.Attempts++;
        job.LastError = reason;
        job.NextAttemptAt = now + FarSpeakerRelabelSchedule.Backoff(job.Attempts);
        job.UpdatedAt = now;

        // A room whose end is not known yet is bounded by the job's own age instead.
        var since = job.RoomEndedAt ?? job.CreatedAt;
        if (FarSpeakerRelabelSchedule.ShouldGiveUp(since, now))
            Finish(job, FarSpeakerRelabelJobStatuses.Abandoned, now, reason);
    }

    private static void Complete(FarSpeakerRelabelJob job, DateTime now, int relabeled)
    {
        job.SegmentsRelabeled = relabeled;
        Finish(job, FarSpeakerRelabelJobStatuses.Done, now, null);
    }

    private static void Finish(FarSpeakerRelabelJob job, string status, DateTime now, string? reason)
    {
        job.Status = status;
        job.LastError = reason;
        job.CompletedAt = now;
        job.UpdatedAt = now;
    }

    private DateTime Now() => _time.GetUtcNow().UtcDateTime;
}
