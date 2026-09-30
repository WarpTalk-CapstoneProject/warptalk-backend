using System.Text.Json;
using Microsoft.Extensions.Logging;
using WarpTalk.Shared;
using WarpTalk.Shared.Events;
using WarpTalk.TranslationRoomService.Application.Interfaces;

namespace WarpTalk.TranslationRoomService.Infrastructure.Redis;

public interface IRecordingCompletedStreamMessageHandler
{
    Task<Result> HandleAsync(RedisStreamMessage message, CancellationToken ct);
}

/// <summary>
/// Routes one <c>meeting:domain-events</c> message to the recording lifecycle processor.
///
/// rec-loss: the name is historical — this handled only <c>meeting.recording_completed</c>, and
/// meeting-service now also publishes <c>recording_started</c> and <c>recording_failed</c> on the
/// same stream. Before this change both of those fell into the "unsupported event_type" branch and
/// were dead-lettered, which is how a started-then-lost recording kept leaving no trace. The class
/// and consumer group keep their names so the deployed group's pending entries are not orphaned.
///
/// Any OTHER event type is still refused exactly as before (failure → retried → DLQ → ACK): the
/// stream carries only recording events today, and a new type appearing here should be visible in
/// the DLQ rather than silently acknowledged.
/// </summary>
public sealed class RecordingCompletedStreamMessageHandler : IRecordingCompletedStreamMessageHandler
{
    private readonly IRecordingLifecycleEventProcessor _processor;
    private readonly ILogger<RecordingCompletedStreamMessageHandler> _logger;

    public RecordingCompletedStreamMessageHandler(
        IRecordingLifecycleEventProcessor processor,
        ILogger<RecordingCompletedStreamMessageHandler> logger)
    {
        _processor = processor;
        _logger = logger;
    }

    public async Task<Result> HandleAsync(RedisStreamMessage message, CancellationToken ct)
    {
        try
        {
            if (!message.Values.TryGetValue("event_type", out var eventType) ||
                !IsRecordingEvent(eventType))
            {
                return Result.Failure(
                    $"Unsupported event_type '{eventType}'",
                    ErrorCodes.ValidationError);
            }

            if (!message.Values.TryGetValue("envelope", out var serializedEnvelope))
                return Result.Failure("Recording event is missing envelope", ErrorCodes.ValidationError);

            var result = eventType switch
            {
                MeetingEventTypes.RecordingStarted =>
                    await ProcessAsync<MeetingRecordingStartedEventPayload>(
                        serializedEnvelope, envelope => _processor.ProcessAsync(envelope, ct)),
                MeetingEventTypes.RecordingFailed =>
                    await ProcessAsync<MeetingRecordingFailedEventPayload>(
                        serializedEnvelope, envelope => _processor.ProcessAsync(envelope, ct)),
                _ =>
                    await ProcessAsync<MeetingRecordingCompletedEventPayload>(
                        serializedEnvelope, envelope => _processor.ProcessAsync(envelope, ct)),
            };

            return result.IsSuccess
                ? Result.Success()
                : Result.Failure(result.Error ?? "Recording event processing failed", result.ErrorCode);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(
                ex,
                "Invalid recording event envelope in Redis message {MessageId}",
                message.Id);
            return Result.Failure("Recording event envelope is invalid JSON", ErrorCodes.ValidationError);
        }
    }

    private static bool IsRecordingEvent(string eventType) =>
        eventType is MeetingEventTypes.RecordingStarted
            or MeetingEventTypes.RecordingCompleted
            or MeetingEventTypes.RecordingFailed;

    private static async Task<Result<bool>> ProcessAsync<TPayload>(
        string serializedEnvelope,
        Func<EventEnvelope<TPayload>, Task<Result<bool>>> process)
    {
        var envelope = JsonSerializer.Deserialize<EventEnvelope<TPayload>>(serializedEnvelope);
        if (envelope == null)
            return Result.Failure<bool>("Recording event envelope is null", ErrorCodes.ValidationError);

        return await process(envelope);
    }
}
