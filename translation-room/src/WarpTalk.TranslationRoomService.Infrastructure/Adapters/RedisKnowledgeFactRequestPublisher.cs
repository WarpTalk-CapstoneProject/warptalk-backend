using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using WarpTalk.Shared.Protos;
using WarpTalk.TranslationRoomService.Application.Interfaces;

namespace WarpTalk.TranslationRoomService.Infrastructure.Adapters;

/// <summary>
/// Writes the "knowledge:fact_requests" Redis Stream that warptalk-ai's KnowledgeFactWorker
/// consumes. Field names must match KnowledgeFactRequestMessage.from_redis() in
/// warptalk-ai/shared/schemas.py exactly.
/// </summary>
public class RedisKnowledgeFactRequestPublisher : IKnowledgeFactRequestPublisher
{
    private const string StreamKey = "knowledge:fact_requests";
    private const int StreamMaxLength = 10000;

    /// <summary>
    /// A summary long enough to exceed this is a summary that failed to summarise. The
    /// worker truncates too; bounding it here keeps an unbounded blob off the stream.
    /// </summary>
    private const int MaxTextChars = 32000;

    private readonly IConnectionMultiplexer _redis;
    private readonly WorkspaceService.WorkspaceServiceClient _workspaceClient;
    private readonly ILogger<RedisKnowledgeFactRequestPublisher> _logger;

    public RedisKnowledgeFactRequestPublisher(
        IConnectionMultiplexer redis,
        WorkspaceService.WorkspaceServiceClient workspaceClient,
        ILogger<RedisKnowledgeFactRequestPublisher> logger)
    {
        _redis = redis;
        _workspaceClient = workspaceClient;
        _logger = logger;
    }

    public async Task PublishAsync(
        Guid workspaceId,
        string sourceType,
        Guid sourceId,
        string title,
        string text,
        bool indexSourceText,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        try
        {
            var allowExternalLlm = await ResolveAllowExternalLlmAsync(workspaceId, ct);

            var entries = new NameValueEntry[]
            {
                new("request_id", Guid.NewGuid().ToString()),
                new("workspace_id", workspaceId.ToString()),
                new("source_type", sourceType),
                new("source_id", sourceId.ToString()),
                new("title", title ?? string.Empty),
                new("text", text.Length > MaxTextChars ? text[..MaxTextChars] : text),
                new("external_llm_allowed", allowExternalLlm ? "true" : "false"),
                new("index_source_text", indexSourceText ? "true" : "false"),
                new("retention_state", "active"),
                new("deletion_state", "active"),
                new("timestamp_ms", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)),
            };

            await _redis.GetDatabase().StreamAddAsync(
                StreamKey, entries, maxLength: StreamMaxLength, useApproximateMaxLength: true);

            _logger.LogInformation(
                "Published knowledge fact request for {SourceType} {SourceId} in workspace {WorkspaceId}.",
                sourceType, sourceId, workspaceId);
        }
        catch (Exception ex)
        {
            // Swallowed by contract — see IKnowledgeFactRequestPublisher. The caller is
            // finalizing a meeting, and a missing Knowledge row must not cost it its artifacts.
            _logger.LogWarning(
                ex,
                "Could not publish a knowledge fact request for {SourceType} {SourceId}.",
                sourceType, sourceId);
        }
    }

    /// <summary>
    /// The workspace's own "may this content leave the deployment" flag.
    ///
    /// FAILS CLOSED, unlike TranscriptRedisConsumerService's equivalent, and deliberately.
    /// That one gates embedding, where the fallback provider is local. This one gates sending
    /// a whole meeting summary to OpenAI. Defaulting to allowed when the workspace service
    /// cannot be reached would leak the content of a workspace that may well have forbidden
    /// it; defaulting to denied costs that meeting its facts, which is visible and fixable.
    ///
    /// Not cached: this runs once per meeting, so a cache would save one call per finished
    /// meeting while making a policy change take minutes to take effect.
    /// </summary>
    private async Task<bool> ResolveAllowExternalLlmAsync(Guid workspaceId, CancellationToken ct)
    {
        try
        {
            var response = await _workspaceClient.GetWorkspaceSettingsAsync(
                new GetWorkspaceSettingsRequest { WorkspaceId = workspaceId.ToString() },
                cancellationToken: ct);
            return response.AllowExternalLlm;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Could not read the AI usage policy for workspace {WorkspaceId}; skipping fact extraction.",
                workspaceId);
            return false;
        }
    }
}
