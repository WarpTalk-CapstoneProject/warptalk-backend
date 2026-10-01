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
/// labelled (<see cref="FarSpeakerSources.Host"/>).
///
/// <para>
/// Google's attribution wins over the live one. A segment the alignment cannot attribute with
/// enough overlap is NOT left with whatever name it happens to carry: its name is re-derived from
/// its own stored live hint by the rule the live line used (<see cref="FarSpeakerNames.ResolveLive"/>
/// under the current threshold) — the live name when one exists and was confident enough,
/// "Google Meet participants" otherwise. The name a segment ends with is always backed by stored
/// evidence (Google's transcript, the host, or the live hint), never by a leftover.
/// </para>
/// </summary>
public sealed class FarSpeakerRelabelService : IFarSpeakerRelabelService
{
    /// <summary>Matches transcript_segments.speaker_name's varchar(100).</summary>
    internal const int SpeakerNameMaxLength = FarSpeakerNames.MaxLength;

    /// <summary>Slack around the segments' own span when choosing which conference records to read.</summary>
    internal static readonly TimeSpan WindowSlack = TimeSpan.FromMinutes(10);

    private const int DiscoverBatch = 50;
    private const int ProcessBatch = 20;

    private readonly IFarSpeakerRelabelStore _store;
    private readonly IBridgeRoomLookup _rooms;
    private readonly IMeetTranscriptSource _meet;
    private readonly TimeProvider _time;
    private readonly FarSpeakerNameOptions _names;
    private readonly ILogger<FarSpeakerRelabelService> _logger;

    public FarSpeakerRelabelService(
        IFarSpeakerRelabelStore store,
        IBridgeRoomLookup rooms,
        IMeetTranscriptSource meet,
        ILogger<FarSpeakerRelabelService> logger,
        TimeProvider? time = null,
        FarSpeakerNameOptions? names = null)
    {
        _store = store;
        _rooms = rooms;
        _meet = meet;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _names = names ?? FarSpeakerNameOptions.Default;
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

        var fetch = await _meet.GetEntriesAsync(host.Value, room.WorkspaceId, room.ExternalMeetingUrl!, windowStart, windowEnd, ct);
        if (!string.IsNullOrEmpty(fetch.ErrorCode))
        {
            if (fetch.ErrorCode == MeetConferenceErrorCodes.InvalidMeeting)
            {
                Finish(job, FarSpeakerRelabelJobStatuses.Skipped, now, fetch.ErrorCode);
                return;
            }

            // plugin_not_connected / meet_scope_missing / connection_required: the host may still
            // connect the google_meet plugin or grant the scope, and the entries stay readable for
            // 30 days — Retry backs off and gives up at that horizon. Anything else is transient.
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

        var relabeled = Apply(transcript.Segments, fetch.Entries, anchorMs, now, _names.MinConfidence);
        _logger.LogInformation(
            "Far-speaker relabel for room {RoomId}: {Relabeled} of {Total} stand-in segments named from {Entries} Meet transcript entries",
            job.TranslationRoomId, relabeled, transcript.Segments.Count, fetch.Entries.Count);
        Complete(job, now, relabeled);
    }

    /// <summary>
    /// Aligns and writes. Returns how many segments changed. Internal for the tests.
    /// </summary>
    /// <param name="minConfidence">The live-name threshold, for the segments the alignment leaves alone.</param>
    internal static int Apply(
        IReadOnlyList<TranscriptSegment> segments,
        IReadOnlyList<MeetTranscriptLine> entries,
        long anchorMs,
        DateTime now,
        double minConfidence = FarSpeakerNames.DefaultMinConfidence)
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
        var assigned = new HashSet<Guid>();
        var changed = 0;
        foreach (var assignment in alignment.Assignments)
        {
            if (!byId.TryGetValue(assignment.SegmentId, out var segment)) continue;
            assigned.Add(segment.Id);

            // A participant Google could not name (gone before the roster was read, a phone line)
            // is still one specific participant — and not necessarily the one the live hint named.
            // Its key replaces the hint below, so keeping the live name would pair one person's
            // name with another's key. The fallback is the honest name for "someone over there".
            var name = string.IsNullOrWhiteSpace(assignment.DisplayName)
                ? FarSpeakerNames.Fallback
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

        // Under the overlap floor: Google's transcript says nothing usable about these. Their name
        // is re-derived from their own stored live hint rather than kept as found, so the job
        // finishing never locks in a name the hint (under the current threshold) does not back.
        // An earlier relabel's attribution is relabel output, and stays.
        foreach (var segment in eligible)
        {
            if (assigned.Contains(segment.Id)) continue;
            if (segment.FarSpeakerSource == FarSpeakerSources.GoogleTranscript) continue;

            var name = FarSpeakerNames.ResolveLive(segment.FarSpeakerKey, segment.FarSpeakerConfidence, minConfidence);
            if (segment.SpeakerName == name) continue;

            segment.SpeakerName = name;
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
