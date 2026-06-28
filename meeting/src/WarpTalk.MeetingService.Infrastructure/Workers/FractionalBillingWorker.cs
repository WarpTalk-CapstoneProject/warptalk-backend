using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using WarpTalk.Shared.Protos;
using System.Collections.Concurrent;

namespace WarpTalk.MeetingService.Infrastructure.Workers;

public class FractionalBillingWorker : BackgroundService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly BillingService.BillingServiceClient _billingClient;
    private readonly ILogger<FractionalBillingWorker> _logger;
    private const string StreamKey = "stt:results";
    private const string ConsumerGroupName = "meeting-billing-consumers";
    private readonly string _consumerName = $"billing-{Environment.MachineName}-{Guid.NewGuid().ToString("N")[..8]}";

    // Track accumulated speech duration per Workspace
    // Key: WorkspaceId
    // Value: Accumulated seconds
    private readonly ConcurrentDictionary<string, double> _accumulatedSeconds = new();

    public FractionalBillingWorker(
        IConnectionMultiplexer redis,
        BillingService.BillingServiceClient billingClient,
        ILogger<FractionalBillingWorker> logger)
    {
        _redis = redis;
        _billingClient = billingClient;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var db = _redis.GetDatabase();

        try
        {
            if (!await db.KeyExistsAsync(StreamKey))
            {
                await db.StreamCreateConsumerGroupAsync(StreamKey, ConsumerGroupName, "0-0", true);
            }
            else
            {
                try
                {
                    await db.StreamCreateConsumerGroupAsync(StreamKey, ConsumerGroupName, "0-0", true);
                }
                catch (RedisServerException ex) when (ex.Message.Contains("BUSYGROUP"))
                {
                    // Ignore, group already exists
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to initialize Redis Stream group for billing.");
        }

        // Start a periodic flush task
        _ = Task.Run(() => FlushAccumulatedCreditsAsync(stoppingToken));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var entries = await db.StreamReadGroupAsync(
                    StreamKey, ConsumerGroupName, _consumerName, ">", count: 50);

                foreach (var entry in entries)
                {
                    string workspaceId = string.Empty;
                    int startMs = 0;
                    int endMs = 0;

                    foreach (var value in entry.Values)
                    {
                        if (value.Name == "workspace_id") workspaceId = value.Value.ToString();
                        if (value.Name == "start_ms") int.TryParse(value.Value.ToString(), out startMs);
                        if (value.Name == "end_ms") int.TryParse(value.Value.ToString(), out endMs);
                    }

                    if (!string.IsNullOrEmpty(workspaceId) && endMs > startMs)
                    {
                        double durationSeconds = (endMs - startMs) / 1000.0;
                        
                        _accumulatedSeconds.AddOrUpdate(
                            workspaceId, 
                            durationSeconds, 
                            (_, existing) => existing + durationSeconds);
                    }

                    await db.StreamAcknowledgeAsync(StreamKey, ConsumerGroupName, entry.Id);
                }

                if (entries.Length == 0)
                {
                    await Task.Delay(1000, stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing billing stream.");
                await Task.Delay(2000, stoppingToken);
            }
        }
    }

    private async Task FlushAccumulatedCreditsAsync(CancellationToken ct)
    {
        // Flush every 10 seconds
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);

            foreach (var kvp in _accumulatedSeconds)
            {
                if (kvp.Value >= 1.0)
                {
                    // Deduct whole seconds
                    int secondsToBill = (int)Math.Floor(kvp.Value);
                    
                    if (_accumulatedSeconds.TryUpdate(kvp.Key, kvp.Value - secondsToBill, kvp.Value))
                    {
                        try
                        {
                            await _billingClient.ConsumeCreditsAsync(new ConsumeCreditsRequest
                            {
                                WorkspaceId = kvp.Key,
                                Amount = secondsToBill,
                                ReferenceType = "AI_SPEECH_TRANSLATION",
                                ReferenceId = "stt-stream"
                            }, cancellationToken: ct);
                            
                            _logger.LogInformation("Billed {Seconds} seconds for Workspace {WorkspaceId}", secondsToBill, kvp.Key);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to publish ConsumeCreditsEvent.");
                            // Revert deduction on failure
                            _accumulatedSeconds.AddOrUpdate(kvp.Key, secondsToBill, (_, existing) => existing + secondsToBill);
                        }
                    }
                }
            }
        }
    }
}
