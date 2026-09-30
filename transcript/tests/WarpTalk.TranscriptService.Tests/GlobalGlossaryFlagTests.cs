using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using WarpTalk.Shared.PlatformSettings;
using WarpTalk.TranscriptService.Infrastructure.Redis;
using Xunit;

namespace WarpTalk.TranscriptService.Tests;

/// <summary>
/// flags.global_glossary is evaluated per workspace at every meeting start, under the deploy-time
/// GlobalGlossary:Enabled switch: off (or outside the rollout) keeps the platform glossary out of the
/// prompts without touching the database; on lets it through — same consumer, no restart.
/// </summary>
public sealed class GlobalGlossaryFlagTests
{
    [Fact]
    public async Task The_platform_flag_gates_the_glossary_per_workspace_live()
    {
        var source = new InMemoryPlatformSettingsSource();
        var reader = new PlatformSettingsReader(source, NullLogger<PlatformSettingsReader>.Instance, cacheTtl: TimeSpan.Zero);
        using var provider = new ServiceCollection().AddSingleton<IPlatformSettings>(reader).BuildServiceProvider();
        using var scope = provider.CreateScope();
        var consumer = Consumer(new Dictionary<string, string?>());
        var workspace = Guid.Parse("00000000-0000-0000-0000-000000000001"); // bucket 83 for this flag
        var other = Guid.NewGuid();

        Assert.True(await consumer.GlobalGlossaryEnabledAsync(scope, workspace, CancellationToken.None));

        source.Set(PlatformSettingsCatalog.FlagGlobalGlossary, new FeatureFlagValue { Enabled = false });
        Assert.False(await consumer.GlobalGlossaryEnabledAsync(scope, workspace, CancellationToken.None));
        // Off means no terms, and the database (no IUnitOfWork registered here) is never asked.
        Assert.Empty(await consumer.LoadGlobalTermsAsync(scope, workspace, CancellationToken.None));

        source.Set(PlatformSettingsCatalog.FlagGlobalGlossary, new FeatureFlagValue { Enabled = true, RolloutPercent = 50, AllowWorkspaces = [other.ToString()] });
        Assert.False(await consumer.GlobalGlossaryEnabledAsync(scope, workspace, CancellationToken.None)); // bucket 83 >= 50
        Assert.True(await consumer.GlobalGlossaryEnabledAsync(scope, other, CancellationToken.None));
    }

    [Fact]
    public async Task The_deploy_switch_still_wins_over_the_flag()
    {
        var reader = new PlatformSettingsReader(new InMemoryPlatformSettingsSource(), NullLogger<PlatformSettingsReader>.Instance);
        using var provider = new ServiceCollection().AddSingleton<IPlatformSettings>(reader).BuildServiceProvider();
        using var scope = provider.CreateScope();
        var consumer = Consumer(new Dictionary<string, string?> { ["GlobalGlossary:Enabled"] = "false" });

        Assert.False(await consumer.GlobalGlossaryEnabledAsync(scope, Guid.NewGuid(), CancellationToken.None));
    }

    private static GlossaryStartedEventConsumer Consumer(Dictionary<string, string?> configuration)
        => new(
            NSubstitute.Substitute.For<IConnectionMultiplexer>(),
            new ServiceCollection().BuildServiceProvider(),
            new ConfigurationBuilder().AddInMemoryCollection(configuration).Build(),
            NullLogger<GlossaryStartedEventConsumer>.Instance);
}
