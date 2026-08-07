using System;
using System.Collections.Generic;
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
                var messages = await db.StreamReadGroupAsync(StreamKey, ConsumerGroup, _consumerName, ">", count: 10);
                if (messages.Length == 0)
                {
                    await Task.Delay(2000, stoppingToken);
                    continue;
                }

                foreach (var message in messages)
                {
                    var values = message.Values.ToDictionary(v => v.Name.ToString(), v => v.Value.ToString());

                    using (var scope = _serviceProvider.CreateScope())
                    {
                        var processor = scope.ServiceProvider.GetRequiredService<IDocumentEmbeddingResultProcessor>();
                        await processor.ProcessResultAsync(values, stoppingToken);
                    }

                    await db.StreamAcknowledgeAsync(StreamKey, ConsumerGroup, message.Id);
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
