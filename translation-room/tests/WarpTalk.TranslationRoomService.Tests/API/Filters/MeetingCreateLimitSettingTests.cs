using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StackExchange.Redis;
using WarpTalk.Shared.PlatformSettings;
using WarpTalk.TranslationRoomService.API.Filters;
using WarpTalk.TranslationRoomService.Application.DTOs;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.API.Filters;

/// <summary>
/// limits.meeting_create_per_minute is read by the creation filter on every request, with a
/// per-workspace override: raising it for one workspace takes effect on that workspace's very next
/// creation and on no other workspace.
/// </summary>
public sealed class MeetingCreateLimitSettingTests
{
    private readonly Dictionary<string, long> _counters = new();
    private readonly InMemoryPlatformSettingsSource _source = new();
    private readonly PlatformSettingsReader _reader;
    private readonly RateLimitingFilter _filter;

    public MeetingCreateLimitSettingTests()
    {
        _reader = new PlatformSettingsReader(_source, NullLogger<PlatformSettingsReader>.Instance, cacheTtl: TimeSpan.Zero);
        var database = new Mock<IDatabase>();
        database.Setup(d => d.StringIncrementAsync(It.IsAny<RedisKey>(), It.IsAny<long>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync((RedisKey key, long by, CommandFlags _) => _counters[key.ToString()] = _counters.GetValueOrDefault(key.ToString()) + by);
        var redis = new Mock<IConnectionMultiplexer>();
        redis.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(database.Object);
        _filter = new RateLimitingFilter(NullLogger<RateLimitingFilter>.Instance, redis.Object, _reader);
    }

    [Fact]
    public async Task The_limit_is_the_live_setting_and_a_workspace_override_wins()
    {
        var busy = Guid.NewGuid();
        var other = Guid.NewGuid();

        // Default: five, then 429.
        for (var i = 0; i < 5; i++) Assert.Null(await Create(busy));
        Assert.Equal(429, (await Create(busy) as ObjectResult)!.StatusCode);

        // Raised for this workspace only.
        _source.Set(PlatformSettingsCatalog.MeetingCreatePerMinute, 20, PlatformSettingsRedisKeys.WorkspaceHash(busy));
        Assert.Null(await Create(busy));

        // Lowered platform-wide: other workspaces follow at once, the override still wins.
        _source.Set(PlatformSettingsCatalog.MeetingCreatePerMinute, 1);
        Assert.Null(await Create(other));
        var refused = await Create(other) as ObjectResult;
        Assert.Equal(429, refused!.StatusCode);
        Assert.Contains("Maximum 1 meetings", refused.Value!.ToString(), StringComparison.Ordinal);
        Assert.Null(await Create(busy));
    }

    private async Task<IActionResult?> Create(Guid workspaceId)
    {
        var request = (CreateTranslationRoomRequest)RuntimeHelpers.GetUninitializedObject(typeof(CreateTranslationRoomRequest))
            with { WorkspaceId = workspaceId };
        var context = new ActionExecutingContext(
            new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor()),
            new List<IFilterMetadata>(),
            new Dictionary<string, object?> { ["request"] = request },
            controller: new object());
        await _filter.OnActionExecutionAsync(context, () => Task.FromResult(new ActionExecutedContext(context, new List<IFilterMetadata>(), new object())));
        return context.Result;
    }
}
