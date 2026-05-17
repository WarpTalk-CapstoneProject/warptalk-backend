using System;
using System.Text;
using System.Threading.Tasks;
using StackExchange.Redis;

namespace WarpTalk.TranslationRoomService.Application.Helpers;

public static class TranscriptHelper
{
    public static async Task<string> AssembleTranscriptAsync(Guid roomId, IDatabase db, string redisKey)
    {
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
