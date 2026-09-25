using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Services;

namespace WarpTalk.BillingService.Infrastructure.Services;

/// <summary>
/// Writes a call into warptalk:provider_calls:{UTC day} with HINCRBY, fire-and-forget. HINCRBY
/// is atomic, so every billing replica can count into the same hash, and the existing
/// ProviderCallStatsSyncWorker copies the day into Postgres exactly as it does the AI workers'.
/// </summary>
public sealed class RedisProviderCallRecorder : IProviderCallRecorder
{
    private static readonly TimeSpan KeyTtl = TimeSpan.FromDays(3);

    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisProviderCallRecorder> _logger;
    private readonly TimeProvider _time;

    public RedisProviderCallRecorder(IConnectionMultiplexer redis, ILogger<RedisProviderCallRecorder> logger, TimeProvider? time = null)
    {
        _redis = redis;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public void Record(string provider, string operation, string outcome, long? latencyMs, string? model = null)
    {
        try
        {
            var now = _time.GetUtcNow().UtcDateTime;
            var key = ProviderCallStatsParser.KeyFor(DateOnly.FromDateTime(now));
            var db = _redis.GetDatabase();
            foreach (var (field, increment) in ProviderCallStatsParser.FieldsFor(provider, operation, model, outcome, latencyMs, now))
            {
                db.HashIncrement(key, field, increment, CommandFlags.FireAndForget);
            }

            db.KeyExpire(key, KeyTtl, CommandFlags.FireAndForget);
        }
        catch (Exception ex)
        {
            // A metric must never fail the call it measures.
            _logger.LogDebug(ex, "Provider call could not be recorded ({Provider}/{Operation}).", provider, operation);
        }
    }
}
