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

public sealed class RecordingCompletedStreamMessageHandler : IRecordingCompletedStreamMessageHandler
{
    private readonly IRecordingCompletedEventProcessor _processor;
    private readonly ILogger<RecordingCompletedStreamMessageHandler> _logger;

    public RecordingCompletedStreamMessageHandler(
        IRecordingCompletedEventProcessor processor,
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
                eventType != MeetingEventTypes.RecordingCompleted)
            {
                return Result.Failure(
                    $"Unsupported event_type '{eventType}'",
                    ErrorCodes.ValidationError);
            }

            if (!message.Values.TryGetValue("envelope", out var serializedEnvelope))
                return Result.Failure("Recording event is missing envelope", ErrorCodes.ValidationError);

            var envelope =
                JsonSerializer.Deserialize<EventEnvelope<MeetingRecordingCompletedEventPayload>>(
                    serializedEnvelope);
            if (envelope == null)
                return Result.Failure("Recording event envelope is null", ErrorCodes.ValidationError);

            var result = await _processor.ProcessAsync(envelope, ct);
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
}
