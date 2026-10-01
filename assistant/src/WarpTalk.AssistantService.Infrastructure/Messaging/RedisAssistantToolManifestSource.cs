using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Interfaces;

namespace WarpTalk.AssistantService.Infrastructure.Messaging;

/// <summary>
/// Reads <see cref="AssistantToolConstants.ManifestRedisKey"/>, which ai_assistant_worker SETs on
/// start-up and refreshes every 10 minutes with a 30-minute TTL. A missing key means the worker is
/// not running.
/// </summary>
/// <remarks>
/// Each read, the value or its absence, is held in process for <see cref="CacheTtl"/>, so the tools
/// page costs one Redis GET per replica per minute. A Redis failure answers "no manifest" and is
/// cached the same way, so an outage costs one failed call per minute rather than one per request.
/// </remarks>
public sealed class RedisAssistantToolManifestSource : IAssistantToolManifestSource
{
    public static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisAssistantToolManifestSource> _logger;
    private readonly TimeProvider _clock;
    private CachedRead? _cached;

    public RedisAssistantToolManifestSource(
        IConnectionMultiplexer redis,
        ILogger<RedisAssistantToolManifestSource> logger,
        TimeProvider? clock = null)
    {
        _redis = redis;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<string?> GetManifestJsonAsync(CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        var cached = Volatile.Read(ref _cached);
        if (cached is not null && now - cached.FetchedAt < CacheTtl) return cached.Json;

        string? json;
        try
        {
            var value = await _redis.GetDatabase()
                .StringGetAsync(AssistantToolConstants.ManifestRedisKey)
                .WaitAsync(ct);
            json = value.HasValue ? value.ToString() : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "The assistant tool manifest could not be read from Redis; reporting it unavailable.");
            json = null;
        }

        Volatile.Write(ref _cached, new CachedRead(json, now));
        return json;
    }

    private sealed record CachedRead(string? Json, DateTimeOffset FetchedAt);
}
