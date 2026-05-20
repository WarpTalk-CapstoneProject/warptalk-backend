using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace WarpTalk.TranslationRoomService.Application.Interfaces;

public class RedisStreamMessage
{
    public string Id { get; set; } = string.Empty;
    public Dictionary<string, string> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public interface IRedisStreamRepository
{
    Task EnsureConsumerGroupExistsAsync(string streamName, string groupName, string position = "0-0");
    Task<List<RedisStreamMessage>> ReadGroupAsync(string streamName, string groupName, string consumerName, string position = ">", int count = 10);
    Task AcknowledgeAsync(string streamName, string groupName, string messageId);
    Task AddAsync(string streamName, Dictionary<string, string> values);
}
