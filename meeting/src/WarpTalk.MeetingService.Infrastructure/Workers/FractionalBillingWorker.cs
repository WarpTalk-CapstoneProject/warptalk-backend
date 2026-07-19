using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using WarpTalk.Shared.Protos;

namespace WarpTalk.MeetingService.Infrastructure.Workers;

public class FractionalBillingWorker : BackgroundService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly BillingService.BillingServiceClient _billingClient;
    private readonly ILogger<FractionalBillingWorker> _logger;
    private const string StreamKey = "stt:results";
    private const string ConsumerGroupName = "meeting-billing-consumers";
    private readonly string _consumerName = $"billing-{Environment.MachineName}-{Guid.NewGuid().ToString("N")[..8]}";

    // Accumulated speech duration per workspace lives in Redis (not process memory) so a
    // worker crash/restart between "stream entry acked" and "10s flush" can't silently lose
    // already-consumed seconds — the next instance picks up the same hash and keeps billing it.
    private const string AccumulatedSecondsHashKey = "billing:fractional:accumulated_seconds";

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
            _logger.LogError(ex, "Failed to initialize Redis Stream consumer group for FractionalBillingWorker.");
        }

        // Start background flushing loop
        _ = Task.Run(() => FlushAccumulatedCreditsAsync(stoppingToken), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var entries = await db.StreamReadGroupAsync(
                    StreamKey,
                    ConsumerGroupName,
                    _consumerName,
                    count: 20,
                    noAck: false);

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

                        // Durably persist the accumulation before acking — if this throws, skip
                        // the ack so the entry gets redelivered instead of billed time vanishing.
                        await db.HashIncrementAsync(AccumulatedSecondsHashKey, workspaceId, durationSeconds);
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
        var db = _redis.GetDatabase();

        // Flush every 10 seconds
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);

            HashEntry[] accumulated;
            try
            {
                accumulated = await db.HashGetAllAsync(AccumulatedSecondsHashKey);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to read accumulated billing seconds from Redis.");
                continue;
            }

            foreach (var entry in accumulated)
            {
                string workspaceId = entry.Name.ToString();
                if (!double.TryParse(entry.Value.ToString(), out var accumulatedValue) || accumulatedValue < 1.0)
                    continue;

                int secondsToBill = (int)Math.Floor(accumulatedValue);

                try
                {
                    // Atomically claim these seconds first, so a concurrent increment from the
                    // read loop during billing isn't clobbered by an overwrite. Must use the
                    // double overload (HINCRBYFLOAT) — the field was written as a float by
                    // HashIncrementAsync, and Redis's integer HINCRBY rejects a non-integer value.
                    await db.HashDecrementAsync(AccumulatedSecondsHashKey, workspaceId, (double)secondsToBill);

                    await _billingClient.ConsumeCreditsAsync(new ConsumeCreditsRequest
                    {
                        WorkspaceId = workspaceId,
                        Amount = secondsToBill,
                        ReferenceType = "AI_SPEECH_TRANSLATION",
                        ReferenceId = "stt-stream"
                    }, cancellationToken: ct);

                    _logger.LogInformation("Billed {Seconds} seconds for Workspace {WorkspaceId}", secondsToBill, workspaceId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to publish ConsumeCreditsEvent.");
                    // Revert the claim on failure so the seconds get retried on the next flush.
                    await db.HashIncrementAsync(AccumulatedSecondsHashKey, workspaceId, (double)secondsToBill);
                }
            }
        }
    }
}
