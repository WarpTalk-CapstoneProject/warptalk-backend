using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.AuthService.Application.Interfaces;

namespace WarpTalk.AuthService.Infrastructure.Clients;

/// <summary>
/// Reads the voice catalog straight out of the Redis key tts_worker writes
/// ("voice_catalog:{language}", 6h TTL). Deliberately a raw StackExchange.Redis read and not
/// IDistributedCache: the cache abstraction wraps values in its own hash envelope, so it
/// cannot read a plain string written by a non-.NET producer.
///
/// Kept byte-compatible with TranslationRoomHub.GetVoiceCatalog, which parses the same key
/// the same way — the two must not drift, or the Voice Profiles page and the in-meeting
/// picker would offer different voices.
/// </summary>
public class RedisVoiceCatalogDirectory : IVoiceCatalogDirectory
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisVoiceCatalogDirectory> _logger;

    // The producer writes lowercase keys ({"id","name","gender"}); this mirrors the Gateway's
    // own tolerance rather than assuming a casing contract with a Python writer.
    private static readonly JsonSerializerOptions CatalogJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public RedisVoiceCatalogDirectory(
        IConnectionMultiplexer redis,
        ILogger<RedisVoiceCatalogDirectory> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    public async Task<IReadOnlyList<VoiceCatalogItemDto>> GetAsync(string language, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return Array.Empty<VoiceCatalogItemDto>();
        }

        var normalized = NormalizeLanguage(language);

        try
        {
            var raw = await _redis.GetDatabase().StringGetAsync($"voice_catalog:{normalized}");
            if (raw.IsNullOrEmpty)
            {
                return Array.Empty<VoiceCatalogItemDto>();
            }

            var entries = JsonSerializer.Deserialize<List<CatalogEntry>>((string)raw!, CatalogJsonOptions);
            if (entries == null)
            {
                return Array.Empty<VoiceCatalogItemDto>();
            }

            var items = new List<VoiceCatalogItemDto>(entries.Count);
            foreach (var entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry.Id))
                {
                    continue;
                }
                items.Add(new VoiceCatalogItemDto(entry.Id, entry.Name ?? entry.Id, entry.Gender ?? string.Empty));
            }

            return items;
        }
        catch (Exception ex)
        {
            // An unreadable catalog must not fail the page — the caller renders "no voices
            // available yet", which is the same thing a cold cache looks like.
            _logger.LogWarning(ex, "Voice catalog for {Language} could not be read from Redis.", normalized);
            return Array.Empty<VoiceCatalogItemDto>();
        }
    }

    /// <summary>
    /// "vi-VN" → "vi". The AI worker keys the cache by the bare ISO-639-1 code, but callers
    /// hand us whatever the room/user profile carries, which is often locale-tagged.
    /// </summary>
    private static string NormalizeLanguage(string language)
    {
        var trimmed = language.Trim();
        var separator = trimmed.IndexOfAny(new[] { '-', '_' });
        var bare = separator > 0 ? trimmed[..separator] : trimmed;
        return bare.ToLowerInvariant();
    }

    private sealed record CatalogEntry(string? Id, string? Name, string? Gender);
}
