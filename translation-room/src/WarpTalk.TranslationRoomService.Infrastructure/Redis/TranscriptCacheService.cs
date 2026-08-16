using System.Collections.Generic;
using System.Threading.Tasks;
using StackExchange.Redis;
using WarpTalk.TranslationRoomService.Application.Interfaces;

namespace WarpTalk.TranslationRoomService.Infrastructure.Redis;

public class TranscriptCacheService : ITranscriptCacheService
{
    private readonly IConnectionMultiplexer _redis;

    public TranscriptCacheService(IConnectionMultiplexer redis)
    {
        _redis = redis;
    }

    public async Task<IReadOnlyList<string>> ReadCachedSegmentsAsync(string redisKey)
    {
        var db = _redis.GetDatabase();
        var values = await db.ListRangeAsync(redisKey);
        if (values.Length == 0)
        {
            return [];
        }

        var lines = new List<string>(values.Length);
        foreach (var value in values)
        {
            var line = value.ToString();
            if (!string.IsNullOrWhiteSpace(line))
            {
                lines.Add(line);
            }
        }

        return lines;
    }
}
