using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using WarpTalk.Shared.Protos;
using System.Collections.Concurrent;
using System.Text.Json;

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

        // Subscribe to meeting.billing.stop to immediately flush credits when meeting ends
        try
        {
            var subscriber = _redis.GetSubscriber();
            await subscriber.SubscribeAsync("meeting.billing.stop", async (channel, message) =>
            {
                try
                {
                    using var doc = JsonDocument.Parse(message.ToString());
                    if (doc.RootElement.TryGetProperty("WorkspaceId", out var wsProp))
                    {
                        var workspaceId = wsProp.GetString();
                        if (!string.IsNullOrEmpty(workspaceId))
                        {
                            _logger.LogInformation("Received meeting.billing.stop event for Workspace {WorkspaceId}. Triggering immediate final flush.", workspaceId);
                            await FlushWorkspaceCreditsAsync(workspaceId, isFinal: true);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing meeting.billing.stop Pub/Sub message.");
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to subscribe to meeting.billing.stop channel.");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var entries = await db.StreamReadGroupAsync(
                    StreamKey, ConsumerGroupName, _consumerName, ">", count: 50);

                foreach (var entry in entries)
                {
                    string workspaceId = string.Empty;
                    string roomId = string.Empty;
                    string userId = string.Empty;
                    int startMs = 0;
                    int endMs = 0;

                    foreach (var value in entry.Values)
                    {
                        if (value.Name == "workspace_id") workspaceId = value.Value.ToString();
                        if (value.Name == "room_id") roomId = value.Value.ToString();
                        if (value.Name == "user_id") userId = value.Value.ToString();
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

                        // Track room/user for last seen (for analytics)
                        if (!string.IsNullOrEmpty(roomId))
                            _lastSeenRoomId[workspaceId] = roomId;
                        if (!string.IsNullOrEmpty(userId))
                            _lastSeenUserId[workspaceId] = userId;
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

    // Last seen room/user for analytics
    private readonly ConcurrentDictionary<string, string> _lastSeenRoomId = new();
    private readonly ConcurrentDictionary<string, string> _lastSeenUserId = new();

    private async Task FlushWorkspaceCreditsAsync(string workspaceId, bool isFinal = false)
    {
        if (_accumulatedSeconds.TryGetValue(workspaceId, out double value))
        {
            if (value <= 0) return;

            int secondsToBill = isFinal ? (int)Math.Ceiling(value) : (int)Math.Floor(value);
            if (secondsToBill <= 0) return;

            if (_accumulatedSeconds.TryUpdate(workspaceId, value - secondsToBill, value))
            {
                try
                {
                    _lastSeenRoomId.TryGetValue(workspaceId, out var roomId);
                    _lastSeenUserId.TryGetValue(workspaceId, out var userId);

                    // Use LogUsageOnly — credits already deducted by ReserveCreditsAsync.
                    // This only creates UsageRecord for analytics (Feature Adoption, Cost by AI service)
                    await _billingClient.LogUsageOnlyAsync(new RecordUsageGrpcRequest
                    {
                        HostWorkspaceId = workspaceId,
                        UserId = string.IsNullOrEmpty(userId) ? Guid.Empty.ToString() : userId,
                        UsageType = "voice_translation",
                        Unit = "seconds",
                        Quantity = secondsToBill,
                        CreditsConsumed = secondsToBill,
                        DurationSeconds = secondsToBill,
                        TranslationRoomId = roomId ?? string.Empty,
                    });

                    _logger.LogInformation("Immediately billed {Seconds} seconds for Workspace {WorkspaceId} (isFinal: {IsFinal})", secondsToBill, workspaceId, isFinal);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to publish ConsumeCreditsEvent for Workspace {WorkspaceId} during immediate flush.", workspaceId);
                    // Revert deduction on failure
                    _accumulatedSeconds.AddOrUpdate(workspaceId, secondsToBill, (_, existing) => existing + secondsToBill);
                }
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
                            _lastSeenRoomId.TryGetValue(kvp.Key, out var roomId);
                            _lastSeenUserId.TryGetValue(kvp.Key, out var userId);

                            // Use LogUsageOnly — credits already deducted by ReserveCreditsAsync.
                            // This only creates UsageRecord for analytics (Feature Adoption, Cost by AI service)
                            await _billingClient.LogUsageOnlyAsync(new RecordUsageGrpcRequest
                            {
                                HostWorkspaceId = kvp.Key,
                                UserId = string.IsNullOrEmpty(userId) ? Guid.Empty.ToString() : userId,
                                UsageType = "voice_translation",
                                Unit = "seconds",
                                Quantity = secondsToBill,
                                CreditsConsumed = secondsToBill,
                                DurationSeconds = secondsToBill,
                                TranslationRoomId = roomId ?? string.Empty,
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
