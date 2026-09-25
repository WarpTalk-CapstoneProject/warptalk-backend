using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using System;
using System.Threading.Tasks;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.Shared;
using WarpTalk.Shared.PlatformSettings;

namespace WarpTalk.TranslationRoomService.API.Filters;

public class RateLimitingFilter : IAsyncActionFilter
{
    private readonly ILogger<RateLimitingFilter> _logger;
    private readonly IConnectionMultiplexer _redis;
    private readonly IPlatformSettings? _settings;

    /// <summary>The limit when platform setting limits.meeting_create_per_minute is not set.</summary>
    public const int DefaultMaxRequestsPerMinute = 5;

    public RateLimitingFilter(ILogger<RateLimitingFilter> logger, IConnectionMultiplexer redis, IPlatformSettings? settings = null)
    {
        _logger = logger;
        _redis = redis;
        _settings = settings;
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

            // Live from /admin/settings, per workspace when an override exists — read on each
            // creation so a raised limit for a bulk-scheduling workspace applies at once.
            var maxPerMinute = _settings is null
                ? DefaultMaxRequestsPerMinute
                : await _settings.GetInt32Async(
                    PlatformSettingsCatalog.MeetingCreatePerMinute,
                    DefaultMaxRequestsPerMinute,
                    new SettingContext(request.WorkspaceId));

            if (currentCount > maxPerMinute)
            {
                _logger.LogWarning("Rate limit exceeded for workspace {WorkspaceId}. Cannot create room.", workspaceId);
                context.Result = new ObjectResult(new ApiErrorResponse($"Rate limit exceeded. Maximum {maxPerMinute} meetings per minute.", ErrorCodes.RateLimitExceeded))
                {
                    StatusCode = 429
                };
                return;
            }
        }

        await next();
    }
}
