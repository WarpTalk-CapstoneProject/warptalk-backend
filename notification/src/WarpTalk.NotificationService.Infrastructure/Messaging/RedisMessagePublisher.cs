using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StackExchange.Redis;
using WarpTalk.NotificationService.Domain.Interfaces;

namespace WarpTalk.NotificationService.Infrastructure.Messaging;

public class RedisMessagePublisher : IMessagePublisher
{
    private readonly IConnectionMultiplexer _redis;

    public RedisMessagePublisher(IConnectionMultiplexer redis)
    {
        _redis = redis;
    }

    public async Task PublishAsync<T>(string topic, T message, CancellationToken ct = default)
    {
        var db = _redis.GetDatabase();
        var messageJson = JsonSerializer.Serialize(message);
        
        // We use StreamAddAsync for Fan-out pattern with Redis Streams
        await db.StreamAddAsync(topic, "payload", messageJson);
    }
}
