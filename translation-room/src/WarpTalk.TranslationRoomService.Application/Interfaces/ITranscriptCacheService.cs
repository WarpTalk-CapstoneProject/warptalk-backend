using System;
using System.Threading.Tasks;

namespace WarpTalk.TranslationRoomService.Application.Interfaces;

public interface ITranscriptCacheService
{
    Task<string> AssembleTranscriptAsync(Guid roomId, string redisKey);
}
