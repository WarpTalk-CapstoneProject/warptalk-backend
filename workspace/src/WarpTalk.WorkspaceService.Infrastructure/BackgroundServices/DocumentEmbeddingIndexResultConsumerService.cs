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

        try
        {
            await db.StreamCreateConsumerGroupAsync(StreamKey, ConsumerGroup, "0-0", createStream: true);
        }
        catch (RedisServerException ex) when (ex.Message.Contains("BUSYGROUP", StringComparison.OrdinalIgnoreCase))
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize Redis Stream consumer group for {StreamKey}.", StreamKey);
        }

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
}
