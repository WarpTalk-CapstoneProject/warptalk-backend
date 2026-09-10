using System.Text.Json;
using Microsoft.Extensions.Logging;
using WarpTalk.MeetingService.Application.Interfaces;
using WarpTalk.MeetingService.Domain.Interfaces;
using WarpTalk.Shared.Events;

namespace WarpTalk.MeetingService.Application.Services;

/// <inheritdoc cref="IEgressCompletion" />
public sealed class EgressCompletion : IEgressCompletion
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IRedisService _redisService;
    private readonly ILogger<EgressCompletion> _logger;

    public EgressCompletion(
        IUnitOfWork unitOfWork,
        IRedisService redisService,
        ILogger<EgressCompletion> logger)
    {
        _unitOfWork = unitOfWork;
        _redisService = redisService;
        _logger = logger;
    }

    public async Task<EgressCompletionOutcome> ApplyAsync(JsonElement egressInfo, CancellationToken ct = default)
    {
        // camelCase and snake_case both accepted throughout. LiveKit's Twirp JSON emits camelCase
        // by default, but the proto field names are snake_case and some deployments send those —
        // reading only one spelling is a silent "no recording" the moment the other arrives.
        var egressId = TryGetString(egressInfo, "egressId") ?? TryGetString(egressInfo, "egress_id");
        var roomName = TryGetString(egressInfo, "roomName") ?? TryGetString(egressInfo, "room_name");

        if (string.IsNullOrWhiteSpace(egressId) && string.IsNullOrWhiteSpace(roomName))
            return EgressCompletionOutcome.RoomNotFound;

        // WT-644 — WHY THE ROOM NAME IS A FALLBACK AND NOT AN ALTERNATIVE.
        //
        // The id lookup is the precise one and stays first. But it can only ever match a room that
        // is STILL holding this egress, and the ordinary way a recording ends is the Stop button:
        // MeetingRoomService.SetRecordingAsync stops the egress and clears ActiveEgressId in the
        // same request, seconds before LiveKit's egress_ended webhook arrives here. So the main
        // path through the UI produced RoomNotFound, published nothing, and the meeting ended with
        // no recording artifact — nothing for the record page to play or download.
        //
        // The reconciliation sweep could not save it either: it scans for ActiveEgressId != null,
        // which is precisely the column this room no longer has.
        //
        // roomName is on every EgressInfo LiveKit sends and ProviderRoomName is unique per room, so
        // it identifies the same room the id would have. It runs only when the id found nothing,
        // so a room that IS still recording is matched by its egress and never by its name.
        var room = !string.IsNullOrWhiteSpace(egressId)
            ? await _unitOfWork.MeetingRoomRepository.FirstOrDefaultAsync(r => r.ActiveEgressId == egressId)
            : null;

        if (room == null && !string.IsNullOrWhiteSpace(roomName))
            room = await _unitOfWork.MeetingRoomRepository.FirstOrDefaultAsync(r => r.ProviderRoomName == roomName);

        if (room == null) return EgressCompletionOutcome.RoomNotFound;

        // Only the egress this webhook is about. Clearing unconditionally was safe while the only
        // way in was `ActiveEgressId == egressId`; with the name fallback above it is not, because
        // a room matched by name may already have STARTED A SECOND recording — and cancelling the
        // live one because the previous one just finished uploading is a worse bug than the one
        // being fixed here.
        if (room.ActiveEgressId == egressId) room.ActiveEgressId = null;

        string? fileUrl = null;
        long? fileSizeBytes = null;
        var fileResults = TryGetArray(egressInfo, "fileResults") ?? TryGetArray(egressInfo, "file_results");
        if (fileResults is JsonElement results)
        {
            var first = results.EnumerateArray().FirstOrDefault();
            if (first.ValueKind == JsonValueKind.Object)
            {
                fileUrl = TryGetString(first, "location") ?? TryGetString(first, "filename");
                fileSizeBytes = TryGetInt64(first, "size")
                    ?? TryGetInt64(first, "fileSize")
                    ?? TryGetInt64(first, "file_size");
            }
        }

        // WT-473: when the recording STARTED, which LiveKit has been sending all along and this
        // handler discarded.
        //
        // It is the field that makes "click a transcript line, seek the video" possible at all.
        // Transcript offsets are measured from the first audio chunk the STT pipeline saw
        // (stt_worker._elapsed_ms), and a recording starts whenever the host switched it on — so
        // without a recording origin the two clocks cannot be reconciled, and every seek lands at
        // an offset that varies per meeting.
        var startedAt = ReadEgressStartedAt(egressInfo);

        // A failed or empty egress has no recording artifact. Clearing the id is still correct —
        // the room is not recording any more — but there is nothing to publish.
        //
        // WT-660: say WHY, using LiveKit's own two fields. Both callers previously reported this
        // as one undifferentiated "finished with no recording file", which cannot tell apart:
        //
        //   * EGRESS_COMPLETE with an empty file list — we asked for something unrecordable, e.g.
        //     the composite template never rendered.
        //   * EGRESS_FAILED / EGRESS_ABORTED — LiveKit tried and broke, and `error` says how.
        //   * EGRESS_LIMIT_REACHED — the plan's recording minutes ran out mid-meeting. Start-time
        //     quota refusal is already handled with a real message in LiveKitEgressService; this
        //     is the same wall hit later, and it was completely silent.
        //
        // Those need three different responses from whoever reads the log, and prod spent an
        // investigation unable to choose between them because neither field was ever read. Logged
        // here rather than in EgressReconciliationService so the webhook path — which until now
        // logged nothing at all when it cleared — is covered by the same line.
        if (string.IsNullOrWhiteSpace(egressId) || string.IsNullOrWhiteSpace(fileUrl))
        {
            _logger.LogWarning(
                "Egress {EgressId} for room {RoomName} produced no recording file. "
                + "LiveKit status={Status}, error={Error}. No recording artifact will exist for it.",
                egressId ?? "(none)",
                roomName ?? "(unknown)",
                ReadEgressStatus(egressInfo) ?? "(absent)",
                ReadEgressError(egressInfo) ?? "(none)");

            return EgressCompletionOutcome.Cleared;
        }

        var envelope = DomainEventEnvelope.Create(
            MeetingEventTypes.RecordingCompleted,
            "meeting-service",
            workspaceId: null,
            new MeetingRecordingCompletedEventPayload(
                room.TranslationRoomId,
                egressId,
                fileUrl,
                GetFileFormat(fileUrl),
                fileSizeBytes,
                ContainsRawAudio: true,
                ContainsRawVideo: true,
                StartedAt: startedAt));

        // Publishing the SAME egress id twice — once from the webhook, once from the sweep — is
        // safe and expected: RecordingCompletedEventProcessor treats an artifact that already
        // exists for this egress id as an idempotent redelivery. That is what lets the fallback
        // run unconditionally instead of having to guess whether the webhook got there first.
        var publishResult = await _redisService.PublishStreamMessageAsync(
            "meeting:domain-events",
            new Dictionary<string, string>
            {
                ["event_id"] = envelope.EventId.ToString(),
                ["event_type"] = envelope.EventType,
                ["schema_version"] = envelope.SchemaVersion.ToString(),
                ["envelope"] = JsonSerializer.Serialize(envelope)
            });

        if (!publishResult.IsSuccess)
            throw new InvalidOperationException(
                $"Could not durably publish {MeetingEventTypes.RecordingCompleted}: {publishResult.Error}");

        return EgressCompletionOutcome.Published;
    }

    /// <summary>
    /// WT-473: LiveKit's egress start time, as UTC.
    ///
    /// EgressInfo carries it as a UNIX timestamp in NANOSECONDS — a proto int64, not seconds and
    /// not milliseconds. Reading it as either would put the recording in 1970 or in the year
    /// 56000, and both are the kind of wrong that renders as a plausible-looking date rather than
    /// an error.
    ///
    /// Both spellings are accepted for the same reason the rest of this file accepts both: LiveKit's
    /// Twirp JSON emits camelCase, the proto field names are snake_case, and some deployments send
    /// those. It also tolerates a STRING, because JSON cannot hold an int64 losslessly and some
    /// emitters quote large numbers rather than risk it.
    ///
    /// Returns null rather than a guess when the field is absent or unreadable. A recording with no
    /// known start is un-seekable, which is a state the UI can show honestly; a fabricated start is
    /// a seek that is silently wrong.
    /// </summary>
    private static DateTime? ReadEgressStartedAt(JsonElement egressInfo)
    {
        var nanoseconds = TryGetInt64(egressInfo, "startedAt")
            ?? TryGetInt64(egressInfo, "started_at")
            ?? TryGetInt64FromString(egressInfo, "startedAt")
            ?? TryGetInt64FromString(egressInfo, "started_at");

        // 0 is LiveKit's "not set", not the epoch. An egress that never started reports it, and
        // storing 1970-01-01 would be indistinguishable from a real value downstream.
        if (nanoseconds is null || nanoseconds <= 0) return null;

        return DateTimeOffset.FromUnixTimeMilliseconds(nanoseconds.Value / 1_000_000).UtcDateTime;
    }

    private static long? TryGetInt64FromString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.String
        && long.TryParse(value.GetString(), out var number)
            ? number
            : null;

    /// <summary>
    /// WT-660: LiveKit's <c>EgressStatus</c>, as a name whichever form it arrives in.
    ///
    /// Twirp JSON serialises a proto enum as its name, but the numeric form turns up too — the
    /// same split <c>EgressReconciliationService.IsTerminal</c> already handles. Mapped to the
    /// name here because the whole point is a log line a person reads: "3" says nothing,
    /// "EGRESS_COMPLETE" and "EGRESS_LIMIT_REACHED" are different instructions.
    /// </summary>
    private static string? ReadEgressStatus(JsonElement egressInfo)
    {
        if (!egressInfo.TryGetProperty("status", out var status)) return null;

        if (status.ValueKind == JsonValueKind.String) return status.GetString();

        if (status.ValueKind == JsonValueKind.Number && status.TryGetInt32(out var ordinal))
        {
            // Proto ordinals, per livekit.EgressStatus.
            return ordinal switch
            {
                0 => "EGRESS_STARTING",
                1 => "EGRESS_ACTIVE",
                2 => "EGRESS_ENDING",
                3 => "EGRESS_COMPLETE",
                4 => "EGRESS_FAILED",
                5 => "EGRESS_ABORTED",
                6 => "EGRESS_LIMIT_REACHED",
                _ => ordinal.ToString(),
            };
        }

        return null;
    }

    /// <summary>
    /// WT-660: LiveKit's own explanation, when it has one. Empty string is how the proto says
    /// "no error", so it is normalised to null rather than logged as a blank.
    /// </summary>
    private static string? ReadEgressError(JsonElement egressInfo)
    {
        var error = TryGetString(egressInfo, "error");
        return string.IsNullOrWhiteSpace(error) ? null : error;
    }

    private static JsonElement? TryGetArray(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Array
            ? value
            : null;

    private static string? TryGetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// WT-473: guarded on <c>ValueKind == Number</c>, and that guard is a bug fix.
    ///
    /// <c>JsonElement.TryGetInt64</c> does NOT return false for a quoted number — it THROWS
    /// InvalidOperationException. So a LiveKit payload that quotes an int64 (which emitters do,
    /// because JSON cannot hold one losslessly) took the whole webhook down through this helper,
    /// and a thrown webhook means the recording artifact is never created at all. The field being
    /// unreadable is a small loss; losing the recording is not.
    ///
    /// This applied to <c>size</c>/<c>fileSize</c> before <c>startedAt</c> existed — the crash was
    /// latent, waiting for a deployment that quoted a file size.
    /// </summary>
    private static long? TryGetInt64(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var number)
            ? number
            : null;

    private static string GetFileFormat(string fileUrl)
    {
        var path = Uri.TryCreate(fileUrl, UriKind.Absolute, out var uri)
            ? uri.AbsolutePath
            : fileUrl;
        return Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
    }
}
