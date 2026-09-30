using System.Text.Json;
using WarpTalk.MeetingService.Application.Interfaces;
using WarpTalk.Shared;
using WarpTalk.Shared.Events;

namespace WarpTalk.MeetingService.Application.Services;

/// <summary>
/// The one place that writes a recording event onto <c>meeting:domain-events</c>.
///
/// rec-loss: three callers now publish recording lifecycle events — Started from
/// <c>MeetingRoomService.SetRecordingAsync</c>, Completed/Failed from <c>EgressCompletion</c> (the
/// webhook and the sweep). translation-room's consumer reads the four stream fields below and
/// deserialises <c>envelope</c>; a copy of this shape that drifted in one caller would be an event
/// the consumer silently skips, which is the very "recording vanished" bug these events exist to end.
///
/// Returns the Result rather than throwing, because the callers deliberately disagree on what a
/// failed publish means: completion throws (so LiveKit retries / the sweep retries next tick),
/// start logs and carries on (the egress is already running).
/// </summary>
internal static class MeetingDomainEventStream
{
    public const string StreamName = "meeting:domain-events";
    private const string Producer = "meeting-service";

    public static async Task<Result> PublishAsync<TPayload>(
        IRedisService redisService,
        string eventType,
        TPayload payload)
    {
        var envelope = DomainEventEnvelope.Create(eventType, Producer, workspaceId: null, payload);

        return await redisService.PublishStreamMessageAsync(
            StreamName,
            new Dictionary<string, string>
            {
                ["event_id"] = envelope.EventId.ToString(),
                ["event_type"] = envelope.EventType,
                ["schema_version"] = envelope.SchemaVersion.ToString(),
                ["envelope"] = JsonSerializer.Serialize(envelope)
            });
    }
}
