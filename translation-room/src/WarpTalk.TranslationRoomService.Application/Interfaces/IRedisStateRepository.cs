using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WarpTalk.TranslationRoomService.Application.Interfaces;

public interface IRedisStateRepository
{
    Task<Dictionary<string, string>> GetHashAllAsync(string key);
    Task HashSetAsync(string key, Dictionary<string, string> fields);
    Task<bool> KeyExpireAsync(string key, TimeSpan expiry);
    Task<bool> KeyDeleteAsync(string key);
    Task<string?> HashGetAsync(string key, string field);
    Task<bool> WaitForSignalAsync(string channel, TimeSpan timeout, CancellationToken ct);
    Task<bool> StringSetAsync(string key, string value, TimeSpan? expiry = null);
    Task<string?> StringGetAsync(string key);
    Task<long> PublishAsync(string channel, string message);
}
