using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Memory;
using System.Security.Claims;

namespace WarpTalk.Gateway.Hubs.Filters;

public class AntiAbuseHubFilter : IHubFilter
{
    private readonly IMemoryCache _cache;
    private readonly ILogger<AntiAbuseHubFilter> _logger;

    private const int MaxMessagesPerSecond = 20;
    private const int MaxConnectsPerMinute = 10;

    public AntiAbuseHubFilter(IMemoryCache cache, ILogger<AntiAbuseHubFilter> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    public async ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        var connectionId = invocationContext.Context.ConnectionId;
        var cacheKey = $"spam:{connectionId}";

        var count = _cache.GetOrCreate(cacheKey, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(1);
            return 0;
        });

        if (count >= MaxMessagesPerSecond)
        {
            _logger.LogWarning("[ABUSE_DETECTED] Hub method spam from ConnectionId: {ConnectionId}. Method: {MethodName}",
                connectionId, invocationContext.HubMethodName);

            // Abort the connection entirely to stop abuse
            invocationContext.Context.Abort();
            throw new HubException("Rate limit exceeded.");
        }

        _cache.Set(cacheKey, count + 1, TimeSpan.FromSeconds(1));

        return await next(invocationContext);
    }

    public async Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
    {
        var userId = context.Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value
                  ?? context.Context.User?.FindFirst("sub")?.Value;

        if (!string.IsNullOrEmpty(userId))
        {
            var cacheKey = $"reconnect:{userId}";
            var count = _cache.GetOrCreate(cacheKey, entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1);
                return 0;
            });

            if (count >= MaxConnectsPerMinute)
            {
                _logger.LogWarning("[ABUSE_DETECTED] Connection rate limit exceeded for UserId: {UserId}", userId);
                context.Context.Abort();
                return;
            }

            _cache.Set(cacheKey, count + 1, TimeSpan.FromMinutes(1));
        }

        await next(context);
    }

    public Task OnDisconnectedAsync(
        HubLifetimeContext context, Exception? exception, Func<HubLifetimeContext, Exception?, Task> next)
    {
        return next(context, exception);
    }
}
