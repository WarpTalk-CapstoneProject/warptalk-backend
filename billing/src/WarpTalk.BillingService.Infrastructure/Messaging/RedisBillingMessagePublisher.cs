using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StackExchange.Redis;
using WarpTalk.BillingService.Domain.Interfaces;

namespace WarpTalk.BillingService.Infrastructure.Messaging;

public class RedisBillingMessagePublisher : IBillingMessagePublisher
{
    private readonly IConnectionMultiplexer _redis;

    public RedisBillingMessagePublisher(IConnectionMultiplexer redis)
    {
        _redis = redis;
    }

    public async Task PublishAsync<T>(string topic, T message, CancellationToken ct = default)
    {
        var db = _redis.GetDatabase();
        var json = JsonSerializer.Serialize(message);

        await db.PublishAsync(RedisChannel.Literal(topic), json);
    }
}
