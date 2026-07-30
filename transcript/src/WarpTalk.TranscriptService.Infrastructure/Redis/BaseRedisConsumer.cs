using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace WarpTalk.TranscriptService.Infrastructure.Redis;

public abstract class BaseRedisConsumer : BackgroundService
{
    protected readonly IConnectionMultiplexer _redis;
    protected readonly ILogger _logger;
    
    protected abstract string StreamKey { get; }
    protected abstract string ConsumerGroup { get; }
    protected abstract string ConsumerName { get; }

    public BaseRedisConsumer(IConnectionMultiplexer redis, ILogger logger)
    {
        _redis = redis;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var db = _redis.GetDatabase();

        // Ensure consumer group exists
        try
        {
            await db.StreamCreateConsumerGroupAsync(StreamKey, ConsumerGroup, "0-0", true);
        }
        catch (RedisServerException ex) when (ex.Message.Contains("BUSYGROUP"))
        {
            // Group already exists, ignore
        }

        _logger.LogInformation($"Starting consumer {ConsumerName} on group {ConsumerGroup} for stream {StreamKey}");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // First process pending messages (in case we crashed)
                await ProcessPendingMessages(db, stoppingToken);

                // Then read new messages blocking for 5 seconds
                var messages = await db.StreamReadGroupAsync(
                    StreamKey,
                    ConsumerGroup,
                    ConsumerName,
                    ">", // read new messages
                    count: 10
                );

                if (messages.Length == 0)
                {
                    // If no new messages were available, just delay a bit to avoid hot loops
                    await Task.Delay(100, stoppingToken);
                    continue;
                }

                foreach (var message in messages)
                {
                    var success = await ProcessMessageAsync(message, stoppingToken);
                    if (success)
                    {
                        await db.StreamAcknowledgeAsync(StreamKey, ConsumerGroup, message.Id);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error processing stream {StreamKey}");
                await Task.Delay(1000, stoppingToken);
            }
        }
    }

    private async Task ProcessPendingMessages(IDatabase db, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var pendingMessages = await db.StreamReadGroupAsync(
                StreamKey,
                ConsumerGroup,
                ConsumerName,
                "0-0", // Read pending
                count: 10
            );

            if (pendingMessages.Length == 0)
                break;

            foreach (var message in pendingMessages)
            {
                var success = await ProcessMessageAsync(message, stoppingToken);
                if (success)
                {
                    await db.StreamAcknowledgeAsync(StreamKey, ConsumerGroup, message.Id);
                }
            }
        }
    }

    protected abstract Task<bool> ProcessMessageAsync(StreamEntry message, CancellationToken stoppingToken);
}
