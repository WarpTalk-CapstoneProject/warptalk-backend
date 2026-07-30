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
        await EnsureConsumerGroupAsync(db);

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

    private static async Task EnsureConsumerGroupAsync(IDatabase db)
    {
        try
        {
            await db.StreamCreateConsumerGroupAsync(StreamName, GroupName, "0", createStream: true);
        }
        catch (RedisServerException ex) when (ex.Message.Contains("BUSYGROUP", StringComparison.OrdinalIgnoreCase))
        {
            // Group already exists.
        }
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

        if (resultType is "chunk" or "tool_call_started")
        {
            if (request.Status == "queued")
            {
                request.Status = "processing";
                unitOfWork.MeetingChatAssistantRequestRepository.Update(request);
                await unitOfWork.SaveChangesAsync(ct);
                await notifier.BroadcastAssistantResponsePendingAsync(request.MeetingRoomId, request.Id, ct);
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
            CreatedAt = DateTime.UtcNow
        };

        await unitOfWork.MeetingChatMessageRepository.AddAsync(response, ct);
        request.Status = failed ? "failed" : "completed";
        request.CompletedAt = DateTime.UtcNow;
        unitOfWork.MeetingChatAssistantRequestRepository.Update(request);
        await unitOfWork.SaveChangesAsync(ct);
        await notifier.BroadcastMessageReceivedAsync(request.MeetingRoomId, response.ToDto(), ct);
    }
}
