using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StackExchange.Redis;
using WarpTalk.TranslationRoomService.Application.Interfaces;

namespace WarpTalk.TranslationRoomService.Infrastructure.Redis;

public class RedisStateRepository : IRedisStateRepository
{
    private readonly IConnectionMultiplexer _redis;

    public RedisStateRepository(IConnectionMultiplexer redis)
    {
        _redis = redis;
    }

    private IDatabase GetDb() => _redis.GetDatabase();

    public async Task<Dictionary<string, string>> GetHashAllAsync(string key)
    {
        var entries = await GetDb().HashGetAllAsync(key);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (!entry.Value.IsNull)
            {
                result[entry.Name.ToString()] = entry.Value.ToString();
            }
        }
        return result;
    }

    public async Task HashSetAsync(string key, Dictionary<string, string> fields)
    {
        if (fields == null || fields.Count == 0) return;

        var entries = fields.Select(kv => new HashEntry(kv.Key, kv.Value)).ToArray();
        await GetDb().HashSetAsync(key, entries);
    }

    public async Task<bool> KeyExpireAsync(string key, TimeSpan expiry)
    {
        return await GetDb().KeyExpireAsync(key, expiry);
    }

    public async Task<bool> KeyDeleteAsync(string key)
    {
        return await GetDb().KeyDeleteAsync(key);
    }

    public async Task<string?> HashGetAsync(string key, string field)
    {
        var value = await GetDb().HashGetAsync(key, field);
        return value.HasValue ? value.ToString() : null;
    }

    public async Task<bool> WaitForSignalAsync(string channel, TimeSpan timeout, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscriber = _redis.GetSubscriber();

        await subscriber.SubscribeAsync(RedisChannel.Literal(channel), (chan, msg) =>
        {
            tcs.TrySetResult(true);
        });

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        try
        {
            return await tcs.Task.WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            await subscriber.UnsubscribeAsync(RedisChannel.Literal(channel));
        }
    }

    public async Task<bool> StringSetAsync(string key, string value, TimeSpan? expiry = null)
    {
        return await GetDb().StringSetAsync(key, value, expiry.HasValue ? expiry.Value : default);
    }

    public async Task<string?> StringGetAsync(string key)
    {
        var val = await GetDb().StringGetAsync(key);
        return val.HasValue ? val.ToString() : null;
    }

    public async Task<long> PublishAsync(string channel, string message)
    {
        var subscriber = _redis.GetSubscriber();
        return await subscriber.PublishAsync(RedisChannel.Literal(channel), message);
    }
}
