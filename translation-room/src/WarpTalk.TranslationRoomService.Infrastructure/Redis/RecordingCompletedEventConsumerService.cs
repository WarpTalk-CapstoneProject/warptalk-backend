using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.Interfaces;

namespace WarpTalk.TranslationRoomService.Infrastructure.Redis;

public sealed class RecordingCompletedEventConsumerService : BackgroundService
{
    public const string StreamName = "meeting:domain-events";
    public const string GroupName = "translation-room.recordings.v1";
    public const string DlqStreamName = "meeting:domain-events:translation-room:dlq";

    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(30);
    private const int MaxAttempts = 3;

    private readonly IRedisStreamRepository _redisStreamRepository;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RecordingCompletedEventConsumerService> _logger;

    public RecordingCompletedEventConsumerService(
        IRedisStreamRepository redisStreamRepository,
        IServiceScopeFactory scopeFactory,
        ILogger<RecordingCompletedEventConsumerService> logger)
    {
        _redisStreamRepository = redisStreamRepository;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _redisStreamRepository.EnsureConsumerGroupExistsAsync(StreamName, GroupName);
        var consumerName = $"recording-{Environment.MachineName}-{Guid.NewGuid():N}";

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ConsumeBatchAsync(consumerName, stoppingToken);
                await Task.Delay(250, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to consume recording events from {StreamName}",
                    StreamName);
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
    }

    public async Task ConsumeBatchAsync(string consumerName, CancellationToken ct)
    {
        var stale = await _redisStreamRepository.ClaimStaleAsync(
            StreamName,
            GroupName,
            consumerName,
            StaleAfter,
            count: 10);
        var fresh = await _redisStreamRepository.ReadGroupAsync(
            StreamName,
            GroupName,
            consumerName,
            ">",
            count: 10);

        foreach (var message in stale.Concat(fresh).DistinctBy(item => item.Id))
            await ProcessWithRetryAsync(message, ct);
    }

    private async Task ProcessWithRetryAsync(RedisStreamMessage message, CancellationToken ct)
    {
        Result result = Result.Failure("Recording event was not processed");
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            using var scope = _scopeFactory.CreateScope();
            var handler = scope.ServiceProvider
                .GetRequiredService<IRecordingCompletedStreamMessageHandler>();
            result = await handler.HandleAsync(message, ct);
            if (result.IsSuccess)
            {
                await _redisStreamRepository.AcknowledgeAsync(
                    StreamName,
                    GroupName,
                    message.Id);
                return;
            }

            if (attempt < MaxAttempts)
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), ct);
        }

        var dlqValues = new Dictionary<string, string>(
            message.Values,
            StringComparer.OrdinalIgnoreCase)
        {
            ["original_message_id"] = message.Id,
            ["error_message"] = result.Error ?? "Unknown recording event processing error",
            ["failed_at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString()
        };

        // ACK only after the DLQ entry is durable. If this write fails, the pending entry is
        // intentionally left unacknowledged and XAUTOCLAIM will recover it.
        await _redisStreamRepository.AddAsync(DlqStreamName, dlqValues);
        await _redisStreamRepository.AcknowledgeAsync(StreamName, GroupName, message.Id);
    }
}
