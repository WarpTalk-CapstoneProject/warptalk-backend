using System.Text.Json;
using WarpTalk.MeetingService.Application.Interfaces;
using WarpTalk.MeetingService.Domain.Interfaces;
using WarpTalk.Shared.Events;

namespace WarpTalk.MeetingService.Application.Services;

/// <inheritdoc cref="IEgressCompletion" />
public sealed class EgressCompletion : IEgressCompletion
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IRedisService _redisService;

    public EgressCompletion(IUnitOfWork unitOfWork, IRedisService redisService)
    {
        _unitOfWork = unitOfWork;
        _redisService = redisService;
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

        var room = !string.IsNullOrWhiteSpace(egressId)
            ? await _unitOfWork.MeetingRoomRepository.FirstOrDefaultAsync(r => r.ActiveEgressId == egressId)
            : await _unitOfWork.MeetingRoomRepository.FirstOrDefaultAsync(r => r.ProviderRoomName == roomName);

        if (room == null) return EgressCompletionOutcome.RoomNotFound;

        room.ActiveEgressId = null;

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
        if (string.IsNullOrWhiteSpace(egressId) || string.IsNullOrWhiteSpace(fileUrl))
            return EgressCompletionOutcome.Cleared;

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
