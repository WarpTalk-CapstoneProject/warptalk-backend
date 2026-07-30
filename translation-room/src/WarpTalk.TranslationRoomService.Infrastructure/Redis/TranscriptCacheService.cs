using System;
using System.Text;
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

    public async Task<string> AssembleTranscriptAsync(Guid roomId, string redisKey)
    {
        var db = _redis.GetDatabase();
        var transcriptCount = await db.ListLengthAsync(redisKey);
        
        var sb = new StringBuilder();
        sb.AppendLine($"# WarpTalk Transcription Room - Room: {roomId}");
        sb.AppendLine($"Generated on: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine("---");

        if (transcriptCount > 0)
        {
            var values = await db.ListRangeAsync(redisKey);
            foreach (var val in values)
            {
                sb.AppendLine(val.ToString());
            }
        }
        else
        {
            sb.AppendLine("*No speech transcription recorded.*");
        }

        return sb.ToString();
    }
}
