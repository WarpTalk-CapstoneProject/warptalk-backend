using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Application.Mappers;
using WarpTalk.AssistantService.Domain.Interfaces;

namespace WarpTalk.AssistantService.API.Services;

/// <summary>
/// Consumes "assistant:chat_results" — published by ai_assistant_worker's ChatAssistantWorker
/// as it streams a reply and dispatches tool calls (see warptalk-ai/ai_assistant_worker/chat_worker.py).
/// Field names here must match ChatResultMessage.to_redis() in warptalk-ai/shared/schemas.py.
/// Mirrors the shape of Gateway's AiResultConsumerService for the STT/translation pipeline.
/// </summary>
public class AssistantChatResultConsumerService : BackgroundService
{
    private const string StreamName = "assistant:chat_results";
    private const string GroupName = "assistant-service-consumers";

    private readonly IConnectionMultiplexer _redis;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AssistantChatResultConsumerService> _logger;
    private readonly string _consumerName = $"assistant-service-{Environment.MachineName}-{Guid.NewGuid():N}";

    public AssistantChatResultConsumerService(
        IConnectionMultiplexer redis,
        IServiceScopeFactory scopeFactory,
        ILogger<AssistantChatResultConsumerService> logger)
    {
        _redis = redis;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var db = _redis.GetDatabase();
        await EnsureConsumerGroupAsync(db);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var entries = await db.StreamReadGroupAsync(StreamName, GroupName, _consumerName, count: 10);
                if (entries.Length == 0)
                {
                    await Task.Delay(500, stoppingToken);
                    continue;
                }

                foreach (var entry in entries)
                {
                    try
                    {
                        await ProcessEntryAsync(entry, stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "AssistantChatResultConsumerService: failed to process entry {EntryId}.", entry.Id);
                    }

                    await db.StreamAcknowledgeAsync(StreamName, GroupName, entry.Id);
                }
            }
            catch (OperationCanceledException)
            {
                // shutting down
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AssistantChatResultConsumerService: error in consume loop.");
                await Task.Delay(2000, stoppingToken);
            }
        }
    }

    private async Task EnsureConsumerGroupAsync(IDatabase db)
    {
        try
        {
            await db.StreamCreateConsumerGroupAsync(StreamName, GroupName, "0", createStream: true);
        }
        catch (RedisServerException ex) when (ex.Message.Contains("BUSYGROUP"))
        {
            // Group already exists — fine.
        }
    }

    private async Task ProcessEntryAsync(StreamEntry entry, CancellationToken ct)
    {
        var fields = entry.Values.ToDictionary(v => v.Name.ToString(), v => v.Value.ToString());

        if (!fields.TryGetValue("request_id", out var requestIdStr) || !Guid.TryParse(requestIdStr, out var requestId))
            return;
        if (!fields.TryGetValue("conversation_id", out var conversationIdStr) || !Guid.TryParse(conversationIdStr, out var conversationId))
            return;

        var type = fields.GetValueOrDefault("type", "");
        var content = fields.GetValueOrDefault("content", "");

        using var scope = _scopeFactory.CreateScope();
        var notifier = scope.ServiceProvider.GetRequiredService<IAssistantNotifier>();

        switch (type)
        {
            case "chunk":
                await notifier.BroadcastMessageChunkAsync(conversationId, requestId, content, ct);
                break;

            case "tool_call_started":
                await notifier.BroadcastToolCallStartedAsync(conversationId, requestId, fields.GetValueOrDefault("tool_name", ""), ct);
                break;

            case "tool_call_completed":
                await notifier.BroadcastToolCallCompletedAsync(
                    conversationId, requestId, fields.GetValueOrDefault("tool_name", ""), fields.GetValueOrDefault("tool_status", ""), ct);
                break;

            case "completed":
                await FinalizeMessageAsync(scope, conversationId, requestId, content, fields.GetValueOrDefault("tool_calls_json", ""), failed: false, ct);
                break;

            case "failed":
                await FinalizeMessageAsync(scope, conversationId, requestId, content, "", failed: true, ct);
                break;

            default:
                _logger.LogWarning("AssistantChatResultConsumerService: unknown result type '{Type}'.", type);
                break;
        }
    }

    private async Task FinalizeMessageAsync(
        IServiceScope scope, Guid conversationId, Guid messageId, string content, string toolCallsJson, bool failed, CancellationToken ct)
    {
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var notifier = scope.ServiceProvider.GetRequiredService<IAssistantNotifier>();

        var message = await unitOfWork.AssistantMessageRepository.GetByIdAsync(messageId, ct);
        if (message == null)
        {
            _logger.LogWarning("AssistantChatResultConsumerService: message {MessageId} not found for '{Type}' result.", messageId, failed ? "failed" : "completed");
            return;
        }

        message.Status = failed ? "failed" : "completed";
        message.CompletedAt = DateTime.UtcNow;
        if (!failed)
            message.Content = content;
        if (!string.IsNullOrEmpty(toolCallsJson))
            message.ToolResultsJson = toolCallsJson;

        unitOfWork.AssistantMessageRepository.Update(message);
        await unitOfWork.SaveChangesAsync(ct);

        if (failed)
        {
            await notifier.BroadcastMessageFailedAsync(
                conversationId, messageId, string.IsNullOrEmpty(content) ? "The assistant could not generate a reply." : content, ct);
        }
        else
        {
            await notifier.BroadcastMessageCompletedAsync(conversationId, message.ToDto(), ct);
        }
    }
}
