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
                ContainsRawVideo: true));

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

    private static JsonElement? TryGetArray(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Array
            ? value
            : null;

    private static string? TryGetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? TryGetInt64(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.TryGetInt64(out var number)
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
