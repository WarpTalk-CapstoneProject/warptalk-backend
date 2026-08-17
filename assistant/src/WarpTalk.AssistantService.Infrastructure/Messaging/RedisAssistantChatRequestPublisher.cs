using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StackExchange.Redis;
using WarpTalk.AssistantService.Application.Interfaces;

namespace WarpTalk.AssistantService.Infrastructure.Messaging;

/// <summary>
/// Publishes to the "assistant:chat_requests" Redis Stream that ai_assistant_worker's
/// ChatAssistantWorker consumes. Field names match ChatRequestMessage.from_redis() in
/// warptalk-ai/shared/schemas.py exactly.
/// </summary>
public sealed class RedisAssistantChatRequestPublisher : IAssistantChatRequestPublisher
{
    private const string StreamName = "assistant:chat_requests";

    private readonly IConnectionMultiplexer _redis;

    public RedisAssistantChatRequestPublisher(IConnectionMultiplexer redis)
    {
        _redis = redis;
    }

    public async Task PublishAsync(
        Guid requestId,
        Guid conversationId,
        Guid workspaceId,
        Guid userId,
        string? bearerToken,
        IReadOnlyList<ChatTurnDto> history,
        string? pageContextJson = null,
        string? mentionsJson = null,
        string? imagesJson = null,
        CancellationToken ct = default)
    {
        var db = _redis.GetDatabase();
        var historyJson = JsonSerializer.Serialize(history.Select(h => new { role = h.Role, content = h.Content }));
        var timestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var entries = new NameValueEntry[]
        {
            new("request_id", requestId.ToString()),
            new("conversation_id", conversationId.ToString()),
            new("workspace_id", workspaceId.ToString()),
            new("user_id", userId.ToString()),
            new("origin", "assistant"),
            new("bearer_token", bearerToken ?? ""),
            new("history_json", historyJson),
            new("page_context_json", pageContextJson ?? ""),
            new("mentions_json", mentionsJson ?? ""),
            // WT-474. Field name must stay in step with ChatRequestMessage.from_redis(), which
            // defaults it to "" — an older worker reading a stream that carries it simply ignores
            // it, so this is safe to deploy before the AI side.
            new("images_json", imagesJson ?? ""),
            new("timestamp_ms", timestampMs.ToString(CultureInfo.InvariantCulture)),
        };

        await db.StreamAddAsync(StreamName, entries, maxLength: 10000, useApproximateMaxLength: true);
    }
}
