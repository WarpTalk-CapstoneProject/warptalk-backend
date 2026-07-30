using System.Collections.Generic;
using WarpTalk.Shared;

namespace WarpTalk.MeetingService.Application.Interfaces;

public interface IRedisService
{
    Task<Result<T?>> GetCacheAsync<T>(string key);
    Task<Result> SetCacheAsync<T>(string key, T data, TimeSpan? expiration = null);
    Task<Result> PublishEventAsync<T>(string channel, T data);

    /// <summary>
    /// Adds an entry to a Redis Stream (XADD). Used to hand work off to the Python AI
    /// workers (warptalk-ai/shared/base_worker.py consume loop), mirroring the same
    /// stt:results/translate:results/tts:results pipeline they already consume.
    /// </summary>
    Task<Result> PublishStreamMessageAsync(string stream, Dictionary<string, string> fields);
}
