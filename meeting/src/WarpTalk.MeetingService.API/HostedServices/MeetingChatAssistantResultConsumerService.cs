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
using WarpTalk.MeetingService.Application.Interfaces;
using WarpTalk.MeetingService.Application.Mappers;
using WarpTalk.MeetingService.Domain.Entities;

namespace WarpTalk.MeetingService.API.HostedServices;

/// <summary>
/// Bridges the shared AI chat result stream back into the persisted meeting-chat
/// aggregate. A dedicated consumer group keeps global AssistantService processing
/// independent from meeting-chat response handling.
/// </summary>
public sealed class MeetingChatAssistantResultConsumerService : BackgroundService
{
    private const string StreamName = "assistant:chat_results";
    private const string GroupName = "meeting-chat-consumers";
    private const string DeadLetterStreamName = "assistant:chat_results:meeting-chat-dead-letter";
    private const string RetryHashName = "assistant:chat_results:meeting-chat-retries";
    private const long ReclaimIdleMilliseconds = 30_000;
    private const long MaxAttempts = 5;

    private readonly IConnectionMultiplexer _redis;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MeetingChatAssistantResultConsumerService> _logger;
    private readonly string _consumerName = $"meeting-chat-{Environment.MachineName}-{Guid.NewGuid():N}";

    public MeetingChatAssistantResultConsumerService(
        IConnectionMultiplexer redis,
        IServiceScopeFactory scopeFactory,
        ILogger<MeetingChatAssistantResultConsumerService> logger)
    {
        _redis = redis;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var db = _redis.GetDatabase();
        if (!await EnsureConsumerGroupAsync(db, _logger, stoppingToken))
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
                        StreamName, GroupName, _consumerName, position: ">", count: 10);
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
                            "Failed to process meeting chat assistant result {EntryId} on attempt {Attempt}",
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
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Meeting chat assistant result consumer loop failed");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
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
    /// killing MeetingService rather than just this consumer. Retries with bounded backoff so
    /// the consumer starts on its own once Redis returns.
    /// </summary>
    /// <returns>true once the group exists; false only when the host is shutting down.</returns>
    private static async Task<bool> EnsureConsumerGroupAsync(
        IDatabase db,
        ILogger logger,
        CancellationToken ct)
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
            catch (RedisServerException ex) when (ex.Message.Contains("BUSYGROUP", StringComparison.OrdinalIgnoreCase))
            {
                // Group already exists.
                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception ex)
            {
                attempt++;
                logger.LogError(
                    ex,
                    "MeetingChatAssistantResultConsumerService could not create consumer group {Group} on {Stream} "
                    + "(attempt {Attempt}); retrying in {RetryDelay}. Meeting chat assistant replies are NOT being "
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
        var fields = entry.Values.ToDictionary(
            value => value.Name.ToString(),
            value => value.Value.ToString());
        if (!string.Equals(
                fields.GetValueOrDefault("origin", string.Empty),
                "meeting_chat",
                StringComparison.Ordinal))
            return;

        if (!Guid.TryParse(fields.GetValueOrDefault("request_id"), out var requestId))
            return;

        var resultType = fields.GetValueOrDefault("type", string.Empty);
        using var scope = _scopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<WarpTalk.MeetingService.Domain.Interfaces.IUnitOfWork>();
        var notifier = scope.ServiceProvider.GetRequiredService<IMeetingChatNotifier>();
        var request = await unitOfWork.MeetingChatAssistantRequestRepository.GetByIdAsync(requestId, ct);

        if (request == null || request.Status is "completed" or "failed")
            return;

        // WHICH ROOM ID THE HUB GROUP IS KEYED ON — and it is not this one.
        //
        // MeetingChatHub.JoinMeetingRoom takes the TRANSLATION room id (the one in the URL),
        // looks the meeting room up BY it, and then joins `meeting_chat:{translationRoomId}`.
        // MeetingChatService broadcasts on that same value, because it is what the caller passed.
        // This consumer had only `request.MeetingRoomId` — the meeting room's own primary key —
        // and broadcast on that, so every assistant event was addressed to a group name no client
        // has ever joined.
        //
        // Nothing errored: SignalR delivers to an empty group happily. The room's whole live
        // record of a WarpBot turn — pending, every tool call, every reasoning line, every chunk,
        // and the answer itself — went nowhere, and the answer appeared only because it is
        // persisted and the panel reads it back from history. That is why the trail showed two
        // steps: both of them are seeded by the client, and neither is evidence of a broadcast.
        var room = await unitOfWork.MeetingRoomRepository.GetByIdAsync(request.MeetingRoomId, ct);
        if (room == null)
        {
            // Deliberately not a fallback to request.MeetingRoomId: that is the broken address,
            // and using it would restore the silent failure this exists to end.
            _logger.LogWarning(
                "Meeting room {MeetingRoomId} not found for assistant request {RequestId}; cannot address its chat group.",
                request.MeetingRoomId,
                request.Id);
            return;
        }

        var groupRoomId = room.TranslationRoomId;

        // The model narrating its own step, which is neither a chunk of the answer nor a tool
        // call — and which used to fall through this method entirely and be dropped.
        if (resultType == "reasoning")
        {
            await notifier.BroadcastAssistantReasoningAsync(
                groupRoomId,
                request.Id,
                fields.GetValueOrDefault("tool_detail", ""),
                fields.GetValueOrDefault("content", ""),
                ct);
            return;
        }

        if (resultType == "question")
        {
            await notifier.BroadcastAssistantQuestionAsync(
                groupRoomId,
                request.Id,
                fields.GetValueOrDefault("tool_calls_json", ""),
                ct);
            return;
        }

        // `tool_call_completed` belongs with these and was missing, which is the whole of the
        // reported defect. OpenAI's HOSTED web search never enters the worker's dispatch loop, so
        // no function call is ever dispatched for it — the worker publishes the step by hand off
        // the response stream, and the event carrying the searched target is the COMPLETED one
        // (the started event fires before the item naming the query is on the wire, so its detail
        // is empty). Falling through to the terminal check below, "tool_call_completed" is not
        // "completed", so every web-search event a meeting produced was discarded: the room's
        // trail sat on "Reading your question" for the length of the search while the widget
        // beside it named every site it had read.
        if (resultType is "chunk" or "tool_call_started" or "tool_call_completed")
        {
            // "pending" is accepted for the rows already in the database when this shipped:
            // they were written by the old spelling and would otherwise stay silent forever.
            if (request.Status is "queued" or "pending")
            {
                request.Status = "processing";
                unitOfWork.MeetingChatAssistantRequestRepository.Update(request);
                await unitOfWork.SaveChangesAsync(ct);
                await notifier.BroadcastAssistantResponsePendingAsync(groupRoomId, request.Id, ct);
            }

            // The answer, as it is written. Until this the room saw nothing between the question
            // and the finished reply — the message is not persisted until the turn is over — so a
            // long answer looked like a stall while the widget beside it was visibly writing.
            //
            // Broadcast, not stored: this is a draft. The persisted message that follows is what
            // everyone keeps, and it is the one that survives a reload or a late joiner.
            if (resultType == "chunk")
            {
                var delta = fields.GetValueOrDefault("content", "");
                if (!string.IsNullOrEmpty(delta))
                {
                    await notifier.BroadcastAssistantChunkAsync(
                        groupRoomId,
                        request.Id,
                        delta,
                        ct);
                }
            }

            // The tool name was already on the message and was being dropped. It is the only
            // evidence the room has that WarpBot is working rather than gone, and it is what the
            // client re-arms its deadline on.
            var toolName = fields.GetValueOrDefault("tool_name", "");
            if (resultType == "tool_call_started" && !string.IsNullOrWhiteSpace(toolName))
            {
                await notifier.BroadcastAssistantToolCallStartedAsync(
                    groupRoomId,
                    request.Id,
                    toolName,
                    fields.GetValueOrDefault("tool_detail", ""),
                    ct);
            }

            // Sent as its own event rather than as another "started". The client folds it into the
            // step already running for that tool — filling in a target the started event could not
            // carry — and a second "started" would instead draw the same search twice.
            if (resultType == "tool_call_completed" && !string.IsNullOrWhiteSpace(toolName))
            {
                await notifier.BroadcastAssistantToolCallCompletedAsync(
                    groupRoomId,
                    request.Id,
                    toolName,
                    fields.GetValueOrDefault("tool_detail", ""),
                    ct);
            }

            return;
        }

        if (resultType is not ("completed" or "failed"))
            return;

        var failed = resultType == "failed";
        var content = fields.GetValueOrDefault("content", string.Empty);
        if (string.IsNullOrWhiteSpace(content))
            content = failed
                ? "WarpBot could not generate a response right now."
                : "WarpBot returned an empty response.";

        var response = new MeetingChatMessage
        {
            Id = Guid.NewGuid(),
            MeetingRoomId = request.MeetingRoomId,
            WorkspaceId = request.WorkspaceId,
            SenderDisplayName = "WarpBot",
            SenderType = "assistant",
            MessageType = "assistant_response",
            OriginalLanguage = "en",
            OriginalText = content,
            TranslationEnabled = false,
            IsHidden = false,
            Mentions = "[]",
            CreatedAt = DateTime.UtcNow,
            // Only on a real answer. A failure message is this service's own prose, and hanging
            // the model's citations off it would attribute WarpTalk's apology to somebody's
            // uploaded document.
            SourcesJson = failed ? null : NullIfBlank(fields.GetValueOrDefault("sources_json"))
        };

        await unitOfWork.MeetingChatMessageRepository.AddAsync(response, ct);
        request.Status = failed ? "failed" : "completed";
        request.CompletedAt = DateTime.UtcNow;
        unitOfWork.MeetingChatAssistantRequestRepository.Update(request);
        await unitOfWork.SaveChangesAsync(ct);
        await notifier.BroadcastMessageReceivedAsync(groupRoomId, response.ToDto(), ct);
    }

    /// <summary>
    /// An answer that cited nothing publishes an empty field, and an empty string is not valid
    /// jsonb — it would fail the insert rather than store "no sources".
    /// </summary>
    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
