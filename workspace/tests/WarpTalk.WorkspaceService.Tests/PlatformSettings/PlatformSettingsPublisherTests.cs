using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using StackExchange.Redis;
using WarpTalk.Shared.PlatformSettings;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Infrastructure.Clients;
using Xunit;

namespace WarpTalk.WorkspaceService.Tests.PlatformSettings;

/// <summary>
/// The Redis snapshot layout the readers depend on (platform / plan / workspace hashes), and that a
/// publish failure is reported, never thrown — a write in the console must not 500 because Redis blinked.
/// </summary>
public sealed class PlatformSettingsPublisherTests
{
    [Fact]
    public void Values_land_in_the_hash_of_their_scope_and_unknown_keys_are_dropped()
    {
        var workspace = Guid.NewGuid();
        var layout = RedisPlatformSettingsPublisher.Layout(
        [
            Row(PlatformSettingsCatalog.DocumentUploadMb, "platform", "", "20"),
            Row(PlatformSettingsCatalog.DocumentUploadMb, "plan", "pro", "50"),
            Row(PlatformSettingsCatalog.DocumentUploadMb, "workspace", workspace.ToString(), "80"),
            Row("retired.key", "platform", "", "true"),
            Row(PlatformSettingsCatalog.DocumentUploadMb, "workspace", "not-a-guid", "1"),
        ]);

        Assert.Equal("20", layout[PlatformSettingsRedisKeys.PlatformHash][PlatformSettingsCatalog.DocumentUploadMb]);
        Assert.Equal("50", layout[PlatformSettingsRedisKeys.PlanHash("pro")][PlatformSettingsCatalog.DocumentUploadMb]);
        Assert.Equal("80", layout[PlatformSettingsRedisKeys.WorkspaceHash(workspace)][PlatformSettingsCatalog.DocumentUploadMb]);
        Assert.Equal(3, layout.Count);
        Assert.DoesNotContain("retired.key", layout[PlatformSettingsRedisKeys.PlatformHash].Keys);
    }

    [Fact]
    public async Task A_redis_failure_is_reported_in_the_state_not_thrown()
    {
        var redis = Substitute.For<IConnectionMultiplexer>();
        var database = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);
        database.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "down"));
        var publisher = new RedisPlatformSettingsPublisher(redis, NullLogger<RedisPlatformSettingsPublisher>.Instance);

        Assert.False(await publisher.PublishAsync([Row(PlatformSettingsCatalog.DocumentUploadMb, "platform", "", "20")], 42));
        Assert.False(publisher.State.Healthy);
        Assert.NotNull(publisher.State.Error);
    }

    private static PlatformSettingValue Row(string key, string scopeType, string scopeId, string json) => new()
    {
        SettingKey = key, ScopeType = scopeType, ScopeId = scopeId, ValueJson = json, Version = 1,
    };
}
