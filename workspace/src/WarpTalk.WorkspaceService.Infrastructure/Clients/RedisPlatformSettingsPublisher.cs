using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using WarpTalk.Shared.PlatformSettings;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Domain.Entities;

namespace WarpTalk.WorkspaceService.Infrastructure.Clients;

/// <summary>
/// Writes the snapshot into the <c>platform:settings:v1:*</c> hashes in one Lua script, so a reader
/// never sees half of a publish: the old hashes are dropped and the new ones written atomically,
/// and the whole thing is skipped when Redis already holds a newer version.
///
/// The script touches hashes it discovers from the index set rather than declaring them all as
/// KEYS. That is fine on the single-primary (Sentinel) Redis WarpTalk runs; it would need hash tags
/// on a Redis Cluster.
/// </summary>
public sealed class RedisPlatformSettingsPublisher : IPlatformSettingsPublisher
{
    public const string IndexKey = PlatformSettingsRedisKeys.Prefix + "index";

    private const string PublishScript = """
        local current = tonumber(redis.call('HGET', KEYS[1], ARGV[4]) or '0') or 0
        local incoming = tonumber(ARGV[1])
        if current > incoming then return 0 end
        local data = cjson.decode(ARGV[2])
        local old = redis.call('SMEMBERS', KEYS[2])
        for _, key in ipairs(old) do redis.call('DEL', key) end
        redis.call('DEL', KEYS[2])
        redis.call('DEL', KEYS[1])
        for hashKey, fields in pairs(data) do
            for field, value in pairs(fields) do redis.call('HSET', hashKey, field, value) end
            redis.call('SADD', KEYS[2], hashKey)
        end
        redis.call('HSET', KEYS[1], ARGV[4], ARGV[1])
        redis.call('SADD', KEYS[2], KEYS[1])
        redis.call('PUBLISH', ARGV[3], ARGV[1])
        return 1
        """;

    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisPlatformSettingsPublisher> _logger;
    private readonly TimeProvider _time;
    private PlatformSettingsPublishState _state = new(0, null, true, null);

    public RedisPlatformSettingsPublisher(
        IConnectionMultiplexer redis,
        ILogger<RedisPlatformSettingsPublisher> logger,
        TimeProvider? time = null)
    {
        _redis = redis;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public PlatformSettingsPublishState State => _state;

    /// <summary>{ hash key: { setting key: JSON } } for the snapshot. Public so a test can hold the layout.</summary>
    public static Dictionary<string, Dictionary<string, string>> Layout(IEnumerable<PlatformSettingValue> values)
    {
        var layout = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (PlatformSettingsCatalog.Find(value.SettingKey) is null) continue;
            string? hash = value.ScopeType switch
            {
                PlatformSettingScopeTypes.Platform => PlatformSettingsRedisKeys.PlatformHash,
                PlatformSettingScopeTypes.Plan => PlatformSettingsRedisKeys.PlanHash(value.ScopeId),
                PlatformSettingScopeTypes.Workspace when Guid.TryParse(value.ScopeId, out var id)
                    => PlatformSettingsRedisKeys.WorkspaceHash(id),
                _ => null,
            };
            if (hash is null) continue;
            if (!layout.TryGetValue(hash, out var fields))
            {
                fields = new Dictionary<string, string>(StringComparer.Ordinal);
                layout[hash] = fields;
            }

            fields[value.SettingKey] = value.ValueJson;
        }

        return layout;
    }

    public async Task<bool> PublishAsync(IReadOnlyList<PlatformSettingValue> values, long version, CancellationToken ct = default)
    {
        try
        {
            var payload = JsonSerializer.Serialize(Layout(values));
            var result = await _redis.GetDatabase().ScriptEvaluateAsync(
                PublishScript,
                [PlatformSettingsRedisKeys.PlatformHash, IndexKey],
                [version, payload, PlatformSettingsRedisKeys.ChangedChannel, PlatformSettingsRedisKeys.VersionField]).WaitAsync(ct);

            var applied = (long)result == 1;
            _state = new PlatformSettingsPublishState(version, _time.GetUtcNow().UtcDateTime, true, null);
            if (!applied)
            {
                _logger.LogDebug("Platform settings version {Version} was not published: Redis already holds a newer one.", version);
            }

            return applied;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _state = _state with { Healthy = false, Error = "Redis could not be written; services keep their last values." };
            _logger.LogError(ex, "Platform settings version {Version} could not be published.", version);
            return false;
        }
    }
}

/// <summary>For a host without Redis (local runs): nothing reaches any service.</summary>
public sealed class NullPlatformSettingsPublisher : IPlatformSettingsPublisher
{
    public PlatformSettingsPublishState State { get; private set; } =
        new(0, null, false, "Redis is not configured; changes are stored but reach no service.");

    public Task<bool> PublishAsync(IReadOnlyList<PlatformSettingValue> values, long version, CancellationToken ct = default)
        => Task.FromResult(false);
}
