using WarpTalk.Shared;

namespace WarpTalk.MeetingService.Application.Interfaces;

public interface IRedisService
{
    Task<Result<T?>> GetCacheAsync<T>(string key);
    Task<Result> SetCacheAsync<T>(string key, T data, TimeSpan? expiration = null);
    Task<Result> PublishEventAsync<T>(string channel, T data);
}
