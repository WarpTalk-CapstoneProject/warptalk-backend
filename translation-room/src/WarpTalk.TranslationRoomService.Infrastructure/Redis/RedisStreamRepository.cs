using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using StackExchange.Redis;
using WarpTalk.TranslationRoomService.Application.Interfaces;

namespace WarpTalk.TranslationRoomService.Infrastructure.Redis;

public class RedisStreamRepository : IRedisStreamRepository
{
    private readonly IConnectionMultiplexer _redis;

    public RedisStreamRepository(IConnectionMultiplexer redis)
    {
        _redis = redis;
    }

    private IDatabase GetDb() => _redis.GetDatabase();

    public async Task EnsureConsumerGroupExistsAsync(string streamName, string groupName, string position = "0-0")
    {
        try
        {
            await GetDb().StreamCreateConsumerGroupAsync(streamName, groupName, position, createStream: true);
        }
        catch (RedisServerException ex) when (ex.Message.Contains("BUSYGROUP"))
        {
            // Idempotent: ignore if consumer group already exists
        }
    }

    public async Task<List<RedisStreamMessage>> ReadGroupAsync(
        string streamName, 
        string groupName, 
        string consumerName, 
        string position = ">", 
        int count = 10)
    {
        var entries = await GetDb().StreamReadGroupAsync(streamName, groupName, consumerName, position, count);
        return Map(entries);
    }

    public async Task<List<RedisStreamMessage>> ClaimStaleAsync(
        string streamName,
        string groupName,
        string consumerName,
        TimeSpan minimumIdleTime,
        int count = 10)
    {
        var result = await GetDb().StreamAutoClaimAsync(
            streamName,
            groupName,
            consumerName,
            checked((long)minimumIdleTime.TotalMilliseconds),
            "0-0",
            count);
        return Map(result.ClaimedEntries);
    }

    public async Task AcknowledgeAsync(string streamName, string groupName, string messageId)
    {
        await GetDb().StreamAcknowledgeAsync(streamName, groupName, messageId);
    }

    public async Task AddAsync(string streamName, Dictionary<string, string> values)
    {
        var entries = values.Select(kv => new NameValueEntry(kv.Key, kv.Value)).ToArray();
        await GetDb().StreamAddAsync(streamName, entries);
    }

    private static List<RedisStreamMessage> Map(StreamEntry[] entries) =>
        entries.Select(entry => new RedisStreamMessage
        {
            Id = entry.Id.ToString(),
            Values = entry.Values.ToDictionary(
                value => value.Name.ToString(),
                value => value.Value.ToString(),
                StringComparer.OrdinalIgnoreCase)
        }).ToList();
}
