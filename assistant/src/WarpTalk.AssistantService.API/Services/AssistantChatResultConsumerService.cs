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
    private const string DeadLetterStreamName = "assistant:chat_results:assistant-dead-letter";
    private const string RetryHashName = "assistant:chat_results:assistant-retries";
    private const long ReclaimIdleMilliseconds = 30_000;
    private const long MaxAttempts = 5;

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
        if (!await EnsureConsumerGroupAsync(db, stoppingToken))
            return;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var reclaimed = await db.StreamAutoClaimAsync(
                    StreamName,
                    GroupName,
                    _consumerName,
                    ReclaimIdleMilliseconds,
                    "0-0",
                    count: 10);
                var entries = reclaimed.ClaimedEntries;
                if (entries.Length == 0)
                {
                    entries = await db.StreamReadGroupAsync(
                        StreamName,
                        GroupName,
                        _consumerName,
                        position: ">",
                        count: 10);
                }
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
                        await db.StreamAcknowledgeAsync(StreamName, GroupName, entry.Id);
                        await db.HashDeleteAsync(RetryHashName, entry.Id);
                    }
                    catch (Exception ex)
                    {
                        var attempt = await db.HashIncrementAsync(RetryHashName, entry.Id);
                        _logger.LogError(
                            ex,
                            "AssistantChatResultConsumerService: failed to process entry {EntryId} on attempt {Attempt}.",
                            entry.Id,
                            attempt);

                        if (attempt >= MaxAttempts)
                        {
                            await MoveToDeadLetterAsync(db, entry, ex, attempt);
                            await db.StreamAcknowledgeAsync(StreamName, GroupName, entry.Id);
                            await db.HashDeleteAsync(RetryHashName, entry.Id);
                        }
                    }
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

    /// <summary>
    /// GUARDED: only BUSYGROUP used to be caught here, so an unreachable Redis threw XGROUP
    /// out of <see cref="ExecuteAsync"/> and tripped BackgroundServiceExceptionBehavior.StopHost,
    /// killing AssistantService rather than just this consumer. Retries with bounded backoff so
    /// the consumer starts on its own once Redis returns.
    /// </summary>
    /// <returns>true once the group exists; false only when the host is shutting down.</returns>
    private async Task<bool> EnsureConsumerGroupAsync(IDatabase db, CancellationToken ct)
    {
        var retryDelay = TimeSpan.FromSeconds(2);
        var attempt = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await db.StreamCreateConsumerGroupAsync(StreamName, GroupName, "0", createStream: true);
                return true;
            }
            catch (RedisServerException ex) when (ex.Message.Contains("BUSYGROUP"))
            {
                // Group already exists — fine.
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
                    "AssistantChatResultConsumerService could not create consumer group {Group} on {Stream} "
                    + "(attempt {Attempt}); retrying in {RetryDelay}. Assistant chat replies are NOT being "
                    + "delivered until it succeeds.",
                    GroupName, StreamName, attempt, retryDelay);

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

    private async Task ProcessEntryAsync(StreamEntry entry, CancellationToken ct)
    {
        var fields = entry.Values.ToDictionary(v => v.Name.ToString(), v => v.Value.ToString());
        if (!string.Equals(
                fields.GetValueOrDefault("origin", "assistant"),
                "assistant",
                StringComparison.Ordinal))
            return;

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
                await notifier.BroadcastToolCallStartedAsync(
                    conversationId,
                    requestId,
                    fields.GetValueOrDefault("tool_name", ""),
                    fields.GetValueOrDefault("tool_detail", ""),
                    ct);
                break;

            case "tool_call_completed":
                await notifier.BroadcastToolCallCompletedAsync(
                    conversationId,
                    requestId,
                    fields.GetValueOrDefault("tool_name", ""),
                    fields.GetValueOrDefault("tool_status", ""),
                    fields.GetValueOrDefault("tool_detail", ""),
                    ct);
                break;

            // The model narrating its own step. tool_detail carries the heading and content the
            // paragraph — two existing fields rather than a schema change, because the shape is
            // the same "one line about one step" every other event on this stream carries.
            case "reasoning":
                await notifier.BroadcastReasoningAsync(
                    conversationId,
                    requestId,
                    fields.GetValueOrDefault("tool_detail", ""),
                    content,
                    ct);
                break;

            // The ask_user tool's output is a card, not text. Relayed on its own event so the
            // client never has to find questions inside an assistant message.
            case "question":
                await notifier.BroadcastQuestionAsync(
                    conversationId, requestId, fields.GetValueOrDefault("tool_calls_json", ""), ct);
                break;

            case "completed":
                await FinalizeMessageAsync(scope, conversationId, requestId, content, fields.GetValueOrDefault("tool_calls_json", ""), fields.GetValueOrDefault("sources_json", ""), failed: false, ct);
                break;

            case "failed":
                await FinalizeMessageAsync(scope, conversationId, requestId, content, "", "", failed: true, ct);
                break;

            default:
                _logger.LogWarning("AssistantChatResultConsumerService: unknown result type '{Type}'.", type);
                break;
        }
    }

    private async Task FinalizeMessageAsync(
        IServiceScope scope, Guid conversationId, Guid messageId, string content, string toolCallsJson, string sourcesJson, bool failed, CancellationToken ct)
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
        // Written even when empty is NOT wanted: leaving the previous value would let a retried
        // answer keep chips it no longer earns.
        if (!failed)
            message.SourcesJson = string.IsNullOrWhiteSpace(sourcesJson) ? null : sourcesJson;

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
