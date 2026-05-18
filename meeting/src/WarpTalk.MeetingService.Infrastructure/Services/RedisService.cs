using System;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using WarpTalk.MeetingService.Application.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.MeetingService.Infrastructure.Services;

public class RedisService : IRedisService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisService> _logger;
    private readonly IDatabase _db;

    public RedisService(IConnectionMultiplexer redis, ILogger<RedisService> logger)
    {
        _redis = redis;
        _logger = logger;
        _db = _redis.GetDatabase();
    }

    public async Task<Result<T?>> GetCacheAsync<T>(string key)
    {
        try
        {
            var value = await _db.StringGetAsync(key);
            if (!value.HasValue) return Result.Success<T?>(default);
            return Result.Success(JsonSerializer.Deserialize<T>(value.ToString()));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting cache for key: {Key}", key);
            return Result.Failure<T?>("Error retrieving cache", "REDIS_ERROR");
        }
    }

    public async Task<Result> SetCacheAsync<T>(string key, T data, TimeSpan? expiration = null)
    {
        try
        {
            var serialized = JsonSerializer.Serialize(data);
            if (expiration.HasValue)
            {
                await _db.StringSetAsync(key, serialized, expiration.Value);
            }
            else
            {
                await _db.StringSetAsync(key, serialized);
            }
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting cache for key: {Key}", key);
            return Result.Failure("Error setting cache", "REDIS_ERROR");
        }
    }

    public async Task<Result> PublishEventAsync<T>(string channel, T data)
    {
        try
        {
            var subscriber = _redis.GetSubscriber();
            var serialized = JsonSerializer.Serialize(data);
            await subscriber.PublishAsync(RedisChannel.Literal(channel), serialized);
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error publishing event to channel: {Channel}", channel);
            return Result.Failure("Error publishing event", "REDIS_ERROR");
        }
    }
}
