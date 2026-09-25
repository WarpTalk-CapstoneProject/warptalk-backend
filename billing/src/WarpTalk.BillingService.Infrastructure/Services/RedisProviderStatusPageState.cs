using System.Text.Json;
using StackExchange.Redis;
using WarpTalk.BillingService.Application.Interfaces;

namespace WarpTalk.BillingService.Infrastructure.Services;

/// <summary>
/// Last status-page reading per provider, in Redis so every billing replica serves the same one and
/// a restart does not blank the page. A day-long TTL: a reading older than that is not "current".
/// </summary>
public sealed class RedisProviderStatusPageState : IProviderStatusPageState
{
    public const string KeyPrefix = "billing:provider-status-page:";
    private static readonly TimeSpan Ttl = TimeSpan.FromDays(1);

    private readonly IConnectionMultiplexer _redis;

    public RedisProviderStatusPageState(IConnectionMultiplexer redis)
    {
        _redis = redis;
    }

    public async Task<ProviderStatusPageSnapshot?> GetAsync(string provider, CancellationToken ct = default)
    {
        try
        {
            var value = await _redis.GetDatabase().StringGetAsync(KeyPrefix + provider);
            return value.IsNullOrEmpty ? null : JsonSerializer.Deserialize<ProviderStatusPageSnapshot>(value.ToString());
        }
        catch (Exception ex) when (ex is RedisException or JsonException)
        {
            return null;
        }
    }

    public async Task SetAsync(ProviderStatusPageSnapshot snapshot, CancellationToken ct = default)
        => await _redis.GetDatabase().StringSetAsync(KeyPrefix + snapshot.Provider, JsonSerializer.Serialize(snapshot), Ttl);
}
