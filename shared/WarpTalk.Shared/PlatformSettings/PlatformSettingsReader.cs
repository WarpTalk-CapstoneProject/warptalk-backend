using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace WarpTalk.Shared.PlatformSettings;

/// <summary>Who a value is being read for. Both parts are optional.</summary>
public readonly record struct SettingContext(Guid? WorkspaceId = null, string? PlanSlug = null)
{
    public static readonly SettingContext Platform = default;
}

/// <summary>
/// The live value of a platform setting, as the owning service should use it.
///
/// Resolution, first hit wins: workspace override, plan override, platform value, then the
/// caller's <c>fallback</c> — which is the service's own deploy-time configuration, so a key nobody
/// has set changes nothing — and finally the registry default. A stored value that no longer passes
/// its definition (a bound was tightened, or Redis was written by hand) is ignored, never used.
/// </summary>
public interface IPlatformSettings
{
    /// <summary>The stored value for <paramref name="key"/> after override resolution, or null when none is set.</summary>
    ValueTask<JsonElement?> GetStoredAsync(string key, SettingContext context = default, CancellationToken ct = default);

    ValueTask<bool> GetBooleanAsync(string key, bool? fallback = null, SettingContext context = default, CancellationToken ct = default);

    ValueTask<int> GetInt32Async(string key, int? fallback = null, SettingContext context = default, CancellationToken ct = default);

    ValueTask<decimal> GetDecimalAsync(string key, decimal? fallback = null, SettingContext context = default, CancellationToken ct = default);

    ValueTask<string> GetStringAsync(string key, string? fallback = null, SettingContext context = default, CancellationToken ct = default);

    ValueTask<IReadOnlyList<string>> GetStringListAsync(string key, IReadOnlyList<string>? fallback = null, SettingContext context = default, CancellationToken ct = default);

    /// <summary>A feature flag for a workspace; <paramref name="fallback"/> applies only when the flag is not set.</summary>
    ValueTask<bool> IsEnabledAsync(string flagKey, SettingContext context = default, bool? fallback = null, CancellationToken ct = default);

    // Synchronous reads, for code that cannot await (a rate-limiter partition, a FluentValidation
    // rule, a token generator property). They answer from the last snapshot this process read and,
    // when it is older than the cache TTL, refresh it in the background — never blocking the caller
    // on Redis. Before the first snapshot has arrived they answer with the fallback.

    bool GetBoolean(string key, bool? fallback = null, SettingContext context = default);

    int GetInt32(string key, int? fallback = null, SettingContext context = default);

    string GetString(string key, string? fallback = null, SettingContext context = default);

    IReadOnlyList<string> GetStringList(string key, IReadOnlyList<string>? fallback = null, SettingContext context = default);
}

/// <summary>Reads one published hash. Null means the hash does not exist.</summary>
public interface IPlatformSettingsSource
{
    Task<IReadOnlyDictionary<string, string>?> ReadHashAsync(string hashKey, CancellationToken ct);
}

public sealed class RedisPlatformSettingsSource : IPlatformSettingsSource
{
    private readonly IConnectionMultiplexer _redis;

    public RedisPlatformSettingsSource(IConnectionMultiplexer redis)
    {
        _redis = redis;
    }

    public async Task<IReadOnlyDictionary<string, string>?> ReadHashAsync(string hashKey, CancellationToken ct)
    {
        var entries = await _redis.GetDatabase().HashGetAllAsync(hashKey).WaitAsync(ct);
        if (entries.Length == 0) return null;
        var map = new Dictionary<string, string>(entries.Length, StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (entry.Value.HasValue) map[entry.Name.ToString()] = entry.Value.ToString();
        }

        return map;
    }
}

/// <summary>
/// An in-process source: hashes held in memory and changed with <see cref="Set"/>. For tests (and
/// local runs without Redis) — it is how a test proves a reader uses the live value: set, read,
/// change, read again.
/// </summary>
public sealed class InMemoryPlatformSettingsSource : IPlatformSettingsSource
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string>> _hashes = new(StringComparer.Ordinal);

    /// <summary>Stores <paramref name="value"/> (serialized to JSON) under <paramref name="key"/> in <paramref name="hashKey"/>.</summary>
    public InMemoryPlatformSettingsSource Set<T>(string key, T value, string hashKey = PlatformSettingsRedisKeys.PlatformHash)
    {
        var hash = _hashes.GetOrAdd(hashKey, _ => new ConcurrentDictionary<string, string>(StringComparer.Ordinal));
        hash[key] = JsonSerializer.Serialize(value);
        hash.TryAdd(PlatformSettingsRedisKeys.VersionField, "1");
        return this;
    }

    public InMemoryPlatformSettingsSource Remove(string key, string hashKey = PlatformSettingsRedisKeys.PlatformHash)
    {
        if (_hashes.TryGetValue(hashKey, out var hash)) hash.TryRemove(key, out _);
        return this;
    }

    /// <summary>Drops a whole hash, as an eviction would.</summary>
    public void Evict(string hashKey = PlatformSettingsRedisKeys.PlatformHash) => _hashes.TryRemove(hashKey, out _);

    public Exception? FailWith { get; set; }

    public Task<IReadOnlyDictionary<string, string>?> ReadHashAsync(string hashKey, CancellationToken ct)
    {
        if (FailWith is { } failure) return Task.FromException<IReadOnlyDictionary<string, string>?>(failure);
        return Task.FromResult<IReadOnlyDictionary<string, string>?>(
            _hashes.TryGetValue(hashKey, out var hash) ? new Dictionary<string, string>(hash, StringComparer.Ordinal) : null);
    }
}

/// <summary>For a host with no Redis (local runs, tests): nothing is ever set.</summary>
public sealed class EmptyPlatformSettingsSource : IPlatformSettingsSource
{
    public Task<IReadOnlyDictionary<string, string>?> ReadHashAsync(string hashKey, CancellationToken ct)
        => Task.FromResult<IReadOnlyDictionary<string, string>?>(null);
}

/// <summary>
/// <see cref="IPlatformSettings"/> over the published Redis hashes, each cached in process for
/// <see cref="CacheTtl"/>. A change therefore reaches every replica within the TTL, without a
/// restart and without a subscription that could die silently.
///
/// Failure is soft by design: when Redis cannot be read the last snapshot is kept (or, before the
/// first successful read, nothing is set and every caller gets its fallback). A settings outage
/// must never take a meeting or a login down.
/// </summary>
public sealed class PlatformSettingsReader : IPlatformSettings
{
    public static readonly TimeSpan DefaultCacheTtl = TimeSpan.FromSeconds(10);

    private readonly IPlatformSettingsSource _source;
    private readonly ILogger<PlatformSettingsReader> _logger;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<string, CachedHash> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _refreshing = new(StringComparer.Ordinal);

    public PlatformSettingsReader(
        IPlatformSettingsSource source,
        ILogger<PlatformSettingsReader> logger,
        TimeProvider? clock = null,
        TimeSpan? cacheTtl = null)
    {
        _source = source;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        CacheTtl = cacheTtl ?? DefaultCacheTtl;
    }

    public TimeSpan CacheTtl { get; }

    public async ValueTask<JsonElement?> GetStoredAsync(string key, SettingContext context = default, CancellationToken ct = default)
    {
        var definition = PlatformSettingsCatalog.Find(key);
        if (definition is null) return null;

        if (context.WorkspaceId is { } workspaceId && definition.AllowsScope(SettingScopes.Workspace)
            && Parse(definition, (await ReadAsync(PlatformSettingsRedisKeys.WorkspaceHash(workspaceId), keepStale: false, ct)), key) is { } ws)
            return ws;

        if (!string.IsNullOrWhiteSpace(context.PlanSlug) && definition.AllowsScope(SettingScopes.Plan)
            && Parse(definition, (await ReadAsync(PlatformSettingsRedisKeys.PlanHash(context.PlanSlug), keepStale: false, ct)), key) is { } plan)
            return plan;

        return Parse(definition, await ReadAsync(PlatformSettingsRedisKeys.PlatformHash, keepStale: true, ct), key);
    }

    public async ValueTask<bool> GetBooleanAsync(string key, bool? fallback = null, SettingContext context = default, CancellationToken ct = default)
        => await GetStoredAsync(key, context, ct) is { } value
            ? value.ValueKind == JsonValueKind.True
            : fallback ?? DefaultOf(key).ValueKind == JsonValueKind.True;

    public async ValueTask<int> GetInt32Async(string key, int? fallback = null, SettingContext context = default, CancellationToken ct = default)
        => await GetStoredAsync(key, context, ct) is { } value && value.TryGetInt32(out var number)
            ? number
            : fallback ?? DefaultOf(key).GetInt32();

    public async ValueTask<decimal> GetDecimalAsync(string key, decimal? fallback = null, SettingContext context = default, CancellationToken ct = default)
        => await GetStoredAsync(key, context, ct) is { } value && value.TryGetDecimal(out var number)
            ? number
            : fallback ?? DefaultOf(key).GetDecimal();

    public async ValueTask<string> GetStringAsync(string key, string? fallback = null, SettingContext context = default, CancellationToken ct = default)
        => await GetStoredAsync(key, context, ct) is { ValueKind: JsonValueKind.String } value
            ? value.GetString()!
            : fallback ?? DefaultOf(key).GetString() ?? string.Empty;

    public async ValueTask<IReadOnlyList<string>> GetStringListAsync(string key, IReadOnlyList<string>? fallback = null, SettingContext context = default, CancellationToken ct = default)
        => await GetStoredAsync(key, context, ct) is { ValueKind: JsonValueKind.Array } value
            ? value.EnumerateArray().Select(item => item.GetString()!).ToArray()
            : fallback ?? DefaultOf(key).EnumerateArray().Select(item => item.GetString()!).ToArray();

    public async ValueTask<bool> IsEnabledAsync(string flagKey, SettingContext context = default, bool? fallback = null, CancellationToken ct = default)
    {
        var stored = await GetStoredAsync(flagKey, new SettingContext(null, null), ct);
        if (stored is null && fallback is { } explicitFallback) return explicitFallback;
        var flag = FeatureFlagValue.TryParse(stored ?? DefaultOf(flagKey)) ?? FeatureFlagValue.Off();
        return flag.IsEnabledFor(flagKey, context.WorkspaceId, context.PlanSlug);
    }

    public bool GetBoolean(string key, bool? fallback = null, SettingContext context = default)
        => PeekStored(key, context) is { } value
            ? value.ValueKind == JsonValueKind.True
            : fallback ?? DefaultOf(key).ValueKind == JsonValueKind.True;

    public int GetInt32(string key, int? fallback = null, SettingContext context = default)
        => PeekStored(key, context) is { } value && value.TryGetInt32(out var number)
            ? number
            : fallback ?? DefaultOf(key).GetInt32();

    public string GetString(string key, string? fallback = null, SettingContext context = default)
        => PeekStored(key, context) is { ValueKind: JsonValueKind.String } value
            ? value.GetString()!
            : fallback ?? DefaultOf(key).GetString() ?? string.Empty;

    public IReadOnlyList<string> GetStringList(string key, IReadOnlyList<string>? fallback = null, SettingContext context = default)
        => PeekStored(key, context) is { ValueKind: JsonValueKind.Array } value
            ? value.EnumerateArray().Select(item => item.GetString()!).ToArray()
            : fallback ?? DefaultOf(key).EnumerateArray().Select(item => item.GetString()!).ToArray();

    /// <summary>Forget every cached hash, so the next read goes to the source.</summary>
    public void Invalidate() => _cache.Clear();

    /// <summary>
    /// Re-read the platform hash and every other hash this process has read before, now. The
    /// synchronous getters then answer from the fresh snapshot. Used at start-up by hosts that read
    /// synchronously, and by tests.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        var keys = _cache.Keys.Append(PlatformSettingsRedisKeys.PlatformHash).Distinct(StringComparer.Ordinal).ToArray();
        foreach (var hashKey in keys)
        {
            if (_cache.TryGetValue(hashKey, out var stale))
                _cache[hashKey] = stale with { FetchedAt = DateTimeOffset.MinValue };
            await ReadAsync(hashKey, keepStale: hashKey == PlatformSettingsRedisKeys.PlatformHash, ct);
        }
    }

    private JsonElement? PeekStored(string key, SettingContext context)
    {
        var definition = PlatformSettingsCatalog.Find(key);
        if (definition is null) return null;

        if (context.WorkspaceId is { } workspaceId && definition.AllowsScope(SettingScopes.Workspace)
            && Parse(definition, Peek(PlatformSettingsRedisKeys.WorkspaceHash(workspaceId), keepStale: false), key) is { } ws)
            return ws;

        if (!string.IsNullOrWhiteSpace(context.PlanSlug) && definition.AllowsScope(SettingScopes.Plan)
            && Parse(definition, Peek(PlatformSettingsRedisKeys.PlanHash(context.PlanSlug), keepStale: false), key) is { } plan)
            return plan;

        return Parse(definition, Peek(PlatformSettingsRedisKeys.PlatformHash, keepStale: true), key);
    }

    /// <summary>The cached hash whatever its age; a stale or missing one is refreshed in the background.</summary>
    private IReadOnlyDictionary<string, string>? Peek(string hashKey, bool keepStale)
    {
        var now = _clock.GetUtcNow();
        _cache.TryGetValue(hashKey, out var cached);
        if (cached is null || now - cached.FetchedAt >= CacheTtl)
        {
            if (_refreshing.TryAdd(hashKey, 0))
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await ReadAsync(hashKey, keepStale, CancellationToken.None);
                    }
                    finally
                    {
                        _refreshing.TryRemove(hashKey, out _);
                    }
                });
            }
        }

        return cached?.Values;
    }

    private static JsonElement DefaultOf(string key)
        => PlatformSettingsCatalog.Find(key)?.Default
           ?? throw new ArgumentException($"'{key}' is not a registered platform setting.", nameof(key));

    private JsonElement? Parse(SettingDefinition definition, IReadOnlyDictionary<string, string>? hash, string key)
    {
        if (hash is null || !hash.TryGetValue(key, out var raw)) return null;
        try
        {
            using var document = JsonDocument.Parse(raw);
            var value = document.RootElement.Clone();
            if (SettingValueValidator.Validate(definition, value) is { } error)
            {
                _logger.LogWarning(
                    "Ignoring the published value of {SettingKey}: {Error}. The fallback applies instead.", key, error);
                return null;
            }

            return value;
        }
        catch (JsonException)
        {
            _logger.LogWarning("Ignoring the published value of {SettingKey}: it is not JSON.", key);
            return null;
        }
    }

    private async ValueTask<IReadOnlyDictionary<string, string>?> ReadAsync(string hashKey, bool keepStale, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        if (_cache.TryGetValue(hashKey, out var cached) && now - cached.FetchedAt < CacheTtl)
            return cached.Values;

        try
        {
            var fresh = await _source.ReadHashAsync(hashKey, ct);
            // A missing platform hash means it was evicted or Redis restarted — not that every value
            // was reset (a reset leaves the version field behind). Keep what we had until the writer's
            // next re-publish instead of silently flipping every setting back to its default.
            var values = fresh is null && keepStale && cached is not null ? cached.Values : fresh;
            _cache[hashKey] = new CachedHash(values, now);
            return values;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Platform settings could not be read from {HashKey}; using the last known values.", hashKey);
            // Back off for one TTL so a Redis outage costs one failed call per hash per TTL, not one per request.
            var values = cached?.Values;
            _cache[hashKey] = new CachedHash(values, now);
            return values;
        }
    }

    private sealed record CachedHash(IReadOnlyDictionary<string, string>? Values, DateTimeOffset FetchedAt);
}
