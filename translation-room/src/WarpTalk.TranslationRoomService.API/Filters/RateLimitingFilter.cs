using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using System;
using System.Threading.Tasks;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.TranslationRoomService.API.Filters;

public class RateLimitingFilter : IAsyncActionFilter
{
    private readonly ILogger<RateLimitingFilter> _logger;
    private readonly IConnectionMultiplexer _redis;
    private const int MaxRequestsPerMinute = 5;

    public RateLimitingFilter(ILogger<RateLimitingFilter> logger, IConnectionMultiplexer redis)
    {
        _logger = logger;
        _redis = redis;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (context.ActionArguments.TryGetValue("request", out var requestObj) && requestObj is CreateTranslationRoomRequest request)
        {
            var workspaceId = request.WorkspaceId.ToString();
            var db = _redis.GetDatabase();
            var rateLimitKey = $"ratelimit:workspace:{workspaceId}:createmeeting";

            // Simple fixed window rate limit using Redis increment
            var currentCount = await db.StringIncrementAsync(rateLimitKey);
            if (currentCount == 1)
            {
                await db.KeyExpireAsync(rateLimitKey, TimeSpan.FromMinutes(1));
            }

            if (currentCount > MaxRequestsPerMinute)
            {
                _logger.LogWarning("Rate limit exceeded for workspace {WorkspaceId}. Cannot create room.", workspaceId);
                context.Result = new ObjectResult(new ApiErrorResponse("Rate limit exceeded. Maximum 5 meetings per minute.", ErrorCodes.RateLimitExceeded))
                {
                    StatusCode = 429
                };
                return;
            }
        }

        await next();
    }
}
