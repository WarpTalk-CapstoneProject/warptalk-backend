using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using WarpTalk.WorkspaceService.Application.Interfaces;

namespace WarpTalk.WorkspaceService.Infrastructure.BackgroundServices;

/// <summary>
/// Consumes embedding:index_results from warptalk-ai and delegates document ingestion status
/// updates to IDocumentEmbeddingResultProcessor for clean architecture SRP.
/// </summary>
public class DocumentEmbeddingIndexResultConsumerService : BackgroundService
{
    private const string StreamKey = "embedding:index_results";
    private const string ConsumerGroup = "workspace-document-embedding-results";
    private const string DeadLetterStreamName = "embedding:index_results:dead-letter";
    private const string RetryHashName = "embedding:index_results:workspace-retries";

    /// <summary>
    /// How long an entry may sit unacknowledged before another consumer may take it.
    ///
    /// The consumer name carries a fresh GUID per process, so on every restart the entries the
    /// previous process had in flight were addressed to a name that no longer exists. Nothing
    /// reclaimed them and <c>"&gt;"</c> only ever returns never-delivered entries, so they were
    /// stranded permanently: production reached 678 pending on this group, flat, with lag 0.
    /// </summary>
    private const long ReclaimIdleMilliseconds = 30_000;

    /// <summary>Attempts before an entry is dead-lettered rather than retried forever.</summary>
    private const long MaxAttempts = 5;

    private readonly string _consumerName = $"workspace-embedding-result-{Environment.MachineName}-{Guid.NewGuid():N}";

    private readonly IConnectionMultiplexer _redis;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<DocumentEmbeddingIndexResultConsumerService> _logger;

    public DocumentEmbeddingIndexResultConsumerService(
        IConnectionMultiplexer redis,
        IServiceProvider serviceProvider,
        ILogger<DocumentEmbeddingIndexResultConsumerService> logger)
    {
        _redis = redis;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("DocumentEmbeddingIndexResultConsumerService started.");
        var db = _redis.GetDatabase();

        // This catch-all already stopped StopHost, but it swallowed once and never retried: after
        // a Redis outage at startup the group was never created, so every StreamReadGroupAsync
        // below failed NOGROUP forever and the service ran deaf while looking alive. Retry with
        // bounded backoff instead, so it recovers on its own once Redis returns.
        if (!await EnsureConsumerGroupAsync(db, stoppingToken))
            return;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Abandoned entries first, new ones only when there are none. Without this a
                // single throw stranded its whole batch: the loop moved on with ">", the
                // unacknowledged entries were addressed to a consumer name that dies with the
                // process, and nothing ever came back for them.
                var reclaimed = await db.StreamAutoClaimAsync(
                    StreamKey,
                    ConsumerGroup,
                    _consumerName,
                    ReclaimIdleMilliseconds,
                    "0-0",
                    count: 10);
                var messages = reclaimed.ClaimedEntries;

                if (messages.Length == 0)
                {
                    messages = await db.StreamReadGroupAsync(
                        StreamKey, ConsumerGroup, _consumerName, ">", count: 10);
                }

                if (messages.Length == 0)
                {
                    await Task.Delay(2000, stoppingToken);
                    continue;
                }

                foreach (var message in messages)
                {
                    // Per entry, so one poison result cannot take the other nine with it. The
                    // batch used to abort on the first throw and every entry in it stayed
                    // pending, which is how a handful of bad results became 678.
                    try
                    {
                        var values = message.Values.ToDictionary(v => v.Name.ToString(), v => v.Value.ToString());

                        using (var scope = _serviceProvider.CreateScope())
                        {
                            var processor = scope.ServiceProvider.GetRequiredService<IDocumentEmbeddingResultProcessor>();
                            await processor.ProcessResultAsync(values, stoppingToken);
                        }

                        await db.StreamAcknowledgeAsync(StreamKey, ConsumerGroup, message.Id);
                        await db.HashDeleteAsync(RetryHashName, message.Id);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        var attempt = await db.HashIncrementAsync(RetryHashName, message.Id);
                        _logger.LogError(
                            ex,
                            "Failed to process embedding index result {EntryId} on attempt {Attempt}.",
                            message.Id,
                            attempt);

                        if (attempt >= MaxAttempts)
                        {
                            await MoveToDeadLetterAsync(db, message, ex, attempt);
                            await db.StreamAcknowledgeAsync(StreamKey, ConsumerGroup, message.Id);
                            await db.HashDeleteAsync(RetryHashName, message.Id);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred in DocumentEmbeddingIndexResultConsumerService processing loop.");
                await Task.Delay(5000, stoppingToken);
            }
        }
    }

    /// <summary>
    /// Parks an entry that has failed <see cref="MaxAttempts"/> times, so the group can move on
    /// without the result being lost. The dead-letter stream is already watched — the
    /// WarpTalkDeadLetterPresent alert fires on any <c>*:dead-letter</c> or <c>*:dlq</c> depth
    /// above zero.
    /// </summary>
    private static Task<RedisValue> MoveToDeadLetterAsync(
        IDatabase db,
        StreamEntry entry,
        Exception exception,
        long attempt)
    {
        var fields = new List<NameValueEntry>(entry.Values)
        {
            new("original_entry_id", entry.Id),
            new("attempt_count", attempt.ToString(CultureInfo.InvariantCulture)),
            new("failed_at", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)),
            new("last_error", exception.Message)
        };

        return db.StreamAddAsync(
            DeadLetterStreamName,
            fields.ToArray(),
            maxLength: 10_000,
            useApproximateMaxLength: true);
    }

    /// <returns>true once the group exists; false only when the host is shutting down.</returns>
    private async Task<bool> EnsureConsumerGroupAsync(IDatabase db, CancellationToken ct)
    {
        var retryDelay = TimeSpan.FromSeconds(2);
        var attempt = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await db.StreamCreateConsumerGroupAsync(StreamKey, ConsumerGroup, "0-0", createStream: true);
                return true;
            }
            catch (RedisServerException ex) when (ex.Message.Contains("BUSYGROUP", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception ex)
            {
                attempt++;
                _logger.LogError(
                    ex,
                    "Failed to initialize Redis Stream consumer group {Group} for {StreamKey} (attempt {Attempt}); "
                    + "retrying in {RetryDelay}. Document embedding results are NOT being processed until it succeeds.",
                    ConsumerGroup, StreamKey, attempt, retryDelay);

                try
                {
                    await Task.Delay(retryDelay, ct);
                }
                catch (OperationCanceledException)
                {
                    return false;
                }

                retryDelay = TimeSpan.FromSeconds(Math.Min(retryDelay.TotalSeconds * 2, 30));
            }
        }

        return false;
    }
}
