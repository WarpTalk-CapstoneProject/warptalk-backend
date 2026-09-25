using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using WarpTalk.Shared.PlatformSettings;
using Xunit;

namespace WarpTalk.WorkspaceService.Tests.PlatformSettings;

/// <summary>
/// The shared registry (WarpTalk.Shared.PlatformSettings): the catalog's own integrity, value
/// validation, feature-flag evaluation (with the bucket vectors warptalk-ai asserts too), the
/// reader's resolution and failure behaviour, and — the rule the registry exists to enforce — that
/// every .NET-owned key is actually read by the service that owns it.
/// </summary>
public sealed class PlatformSettingsRegistryTests
{
    // ── Catalog integrity ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Keys_are_unique_dotted_and_in_a_known_category_and_owner()
    {
        var keys = PlatformSettingsCatalog.All.Select(d => d.Key).ToList();
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());

        var owners = typeof(SettingOwners).GetFields().Select(f => (string)f.GetValue(null)!).ToHashSet();
        foreach (var definition in PlatformSettingsCatalog.All)
        {
            Assert.Matches("^[a-z][a-z0-9_]*(\\.[a-z0-9_]+)+$", definition.Key);
            Assert.True(definition.Key.Length <= 120, definition.Key);
            Assert.Contains(definition.Category, SettingCategories.Ordered);
            Assert.Contains(definition.OwningService, owners);
            Assert.False(string.IsNullOrWhiteSpace(definition.Label), definition.Key);
            Assert.False(string.IsNullOrWhiteSpace(definition.Description), definition.Key);
            Assert.True(definition.Scopes.HasFlag(SettingScopes.Platform), definition.Key);
        }
    }

    [Fact]
    public void Every_default_passes_its_own_definition()
    {
        foreach (var definition in PlatformSettingsCatalog.All)
        {
            Assert.Null(SettingValueValidator.Validate(definition, definition.Default));
        }
    }

    [Fact]
    public void Key_prefix_matches_category_so_the_security_permission_cannot_be_sidestepped()
    {
        var prefixes = new[]
        {
            (SettingCategories.General, "general."), (SettingCategories.Security, "security."),
            (SettingCategories.Meetings, "meetings."), (SettingCategories.Billing, "billing."),
            (SettingCategories.Notifications, "notifications."), (SettingCategories.Limits, "limits."),
            (SettingCategories.FeatureFlags, "flags."),
        };
        foreach (var definition in PlatformSettingsCatalog.All)
        {
            var prefix = prefixes.Single(p => p.Item1 == definition.Category).Item2;
            Assert.StartsWith(prefix, definition.Key);
            Assert.Equal(definition.Category == SettingCategories.Security, definition.RequiresSecurityPermission);
        }
    }

    [Fact]
    public void Flags_are_platform_scoped_because_they_target_workspaces_themselves()
    {
        Assert.All(
            PlatformSettingsCatalog.All.Where(d => d.Type == SettingValueType.FeatureFlag),
            d => Assert.Equal(SettingScopes.Platform, d.Scopes));
    }

    /// <summary>
    /// "Fixes that were never wired": a key only belongs here if its owning service reads it. For
    /// every .NET-owned key the owner's source must name the catalog constant; Python- and web-owned
    /// keys are held by their own repos' tests (warptalk-ai tests/test_platform_settings*.py,
    /// warptalk-web scripts/check-admin-platform-settings-contract.mjs).
    /// </summary>
    [Fact]
    public void Every_dotnet_owned_key_is_read_by_its_owning_service()
    {
        var root = RepoRoot();
        var serviceDirectories = new[]
        {
            (SettingOwners.Gateway, "gateway/src"), (SettingOwners.Auth, "auth/src"),
            (SettingOwners.Workspace, "workspace/src"), (SettingOwners.TranslationRoom, "translation-room/src"),
            (SettingOwners.Meeting, "meeting/src"), (SettingOwners.Billing, "billing/src"),
            (SettingOwners.Notification, "notification/src"), (SettingOwners.Transcript, "transcript/src"),
        }.ToDictionary(p => p.Item1, p => p.Item2);
        var externallyOwned = new[]
        {
            SettingOwners.AiStt, SettingOwners.AiTranslation, SettingOwners.AiTts, SettingOwners.AiSuggest,
            SettingOwners.AiAssistant, SettingOwners.Web,
        };

        var constants = typeof(PlatformSettingsCatalog).GetFields()
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .ToDictionary(f => (string)f.GetRawConstantValue()!, f => f.Name);

        // A shared helper that reads the key counts when the owner calls it (the e-mail sender is
        // resolved by EmailSenderSettings for every service that sends mail).
        var sharedReaders = Directory.EnumerateFiles(Path.Combine(root, "shared", "WarpTalk.Shared"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path) && !path.EndsWith("PlatformSettingsCatalog.cs", StringComparison.Ordinal))
            .Select(path => (Type: Path.GetFileNameWithoutExtension(path), Text: File.ReadAllText(path)))
            .ToList();

        foreach (var definition in PlatformSettingsCatalog.All)
        {
            if (externallyOwned.Contains(definition.OwningService)) continue;
            Assert.True(serviceDirectories.TryGetValue(definition.OwningService, out var directory), definition.Key);
            var constant = constants[definition.Key];
            var reference = new Regex($@"PlatformSettingsCatalog\.{constant}\b");
            var source = Directory.EnumerateFiles(Path.Combine(root, directory), "*.cs", SearchOption.AllDirectories)
                .Where(path => !IsBuildOutput(path))
                .Select(File.ReadAllText)
                .ToList();
            var viaHelpers = sharedReaders.Where(r => reference.IsMatch(r.Text)).Select(r => r.Type).ToList();
            Assert.True(
                source.Any(text => reference.IsMatch(text))
                || viaHelpers.Any(helper => source.Any(text => text.Contains(helper + ".", StringComparison.Ordinal))),
                $"{definition.Key} is owned by {definition.OwningService} but nothing in {directory} reads PlatformSettingsCatalog.{constant}.");
        }
    }

    private static bool IsBuildOutput(string path)
        => path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
           || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    // ── Validation ──────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(PlatformSettingsCatalog.AccessTokenMinutes, "30", true)]
    [InlineData(PlatformSettingsCatalog.AccessTokenMinutes, "4", false)]
    [InlineData(PlatformSettingsCatalog.AccessTokenMinutes, "241", false)]
    [InlineData(PlatformSettingsCatalog.AccessTokenMinutes, "30.5", false)]
    [InlineData(PlatformSettingsCatalog.AccessTokenMinutes, "\"30\"", false)]
    [InlineData(PlatformSettingsCatalog.SuggestMinConfidence, "0.7", true)]
    [InlineData(PlatformSettingsCatalog.SuggestMinConfidence, "1.2", false)]
    [InlineData(PlatformSettingsCatalog.MaintenanceEnabled, "true", true)]
    [InlineData(PlatformSettingsCatalog.MaintenanceEnabled, "1", false)]
    [InlineData(PlatformSettingsCatalog.SupportEmail, "\"help@warptalk.vn\"", true)]
    [InlineData(PlatformSettingsCatalog.SupportEmail, "\"not an email\"", false)]
    [InlineData(PlatformSettingsCatalog.EmailReplyTo, "\"\"", true)]
    [InlineData(PlatformSettingsCatalog.EmailFromName, "\"Evil <x@y.z>\"", false)]
    [InlineData(PlatformSettingsCatalog.MaintenanceAllowlist, "[\"a@b.co\",\"c@d.co\"]", true)]
    [InlineData(PlatformSettingsCatalog.MaintenanceAllowlist, "[\"a@b.co\",\"A@B.CO\"]", false)]
    [InlineData(PlatformSettingsCatalog.MaintenanceAllowlist, "[\"nope\"]", false)]
    [InlineData(PlatformSettingsCatalog.MaintenanceMessage, "\"line\\u0007\"", false)]
    [InlineData(PlatformSettingsCatalog.WorkspaceDefaultTimezone, "\"Asia/Ho_Chi_Minh\"", true)]
    [InlineData(PlatformSettingsCatalog.WorkspaceDefaultTimezone, "\"../etc\"", false)]
    [InlineData(PlatformSettingsCatalog.FlagAiSuggest, "{\"enabled\":true,\"rolloutPercent\":25}", true)]
    [InlineData(PlatformSettingsCatalog.FlagAiSuggest, "{\"enabled\":true,\"rolloutPercent\":101}", false)]
    [InlineData(PlatformSettingsCatalog.FlagAiSuggest, "{\"rolloutPercent\":50}", false)]
    [InlineData(PlatformSettingsCatalog.FlagAiSuggest, "{\"enabled\":true,\"surprise\":1}", false)]
    [InlineData(PlatformSettingsCatalog.FlagAiSuggest, "{\"enabled\":true,\"allowWorkspaces\":[\"not-a-guid\"]}", false)]
    [InlineData(PlatformSettingsCatalog.FlagAiSuggest, "{\"enabled\":true,\"allowPlans\":[\"Pro Plan\"]}", false)]
    [InlineData(PlatformSettingsCatalog.FlagAiSuggest, "{\"enabled\":false,\"allowPlans\":[\"pro\"],\"denyWorkspaces\":[\"3f2504e0-4f89-11d3-9a0c-0305e82c3301\"]}", true)]
    public void Validation_follows_the_definition(string key, string json, bool valid)
    {
        var definition = PlatformSettingsCatalog.Find(key)!;
        using var document = JsonDocument.Parse(json);
        var error = SettingValueValidator.Validate(definition, document.RootElement);
        Assert.True(valid == (error is null), error ?? "expected an error");
    }

    // ── Feature flags ───────────────────────────────────────────────────────────────────────

    /// <summary>Same vectors as warptalk-ai tests/test_platform_settings.py.</summary>
    [Theory]
    [InlineData("flags.ai_suggest", "00000000-0000-0000-0000-000000000001", 76)]
    [InlineData("flags.ai_suggest", "3f2504e0-4f89-11d3-9a0c-0305e82c3301", 14)]
    [InlineData("flags.voice_clone", "9b2d7c1e-8a4f-4e3b-b5d6-1c2e3f4a5b6c", 61)]
    [InlineData("flags.global_glossary", "00000000-0000-0000-0000-000000000001", 83)]
    [InlineData("flags.warpbot_web_search", "FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF", 4)]
    public void Bucket_is_stable_across_languages(string key, string subject, int bucket)
        => Assert.Equal(bucket, FeatureFlagValue.Bucket(key, subject));

    [Fact]
    public void Flag_evaluation_order_kill_switch_deny_allow_then_rollout()
    {
        var inRollout = Guid.Parse("3f2504e0-4f89-11d3-9a0c-0305e82c3301");  // bucket 14
        var outOfRollout = Guid.Parse("00000000-0000-0000-0000-000000000001"); // bucket 76
        var flag = new FeatureFlagValue
        {
            Enabled = true,
            RolloutPercent = 50,
            AllowWorkspaces = [outOfRollout.ToString()],
            DenyWorkspaces = [inRollout.ToString()],
            AllowPlans = ["enterprise"],
        };

        Assert.False(flag.IsEnabledFor(PlatformSettingsCatalog.FlagAiSuggest, inRollout, "enterprise"));   // deny beats allow
        Assert.True(flag.IsEnabledFor(PlatformSettingsCatalog.FlagAiSuggest, outOfRollout, null));         // allow beats rollout
        Assert.True(flag.IsEnabledFor(PlatformSettingsCatalog.FlagAiSuggest, Guid.NewGuid(), "enterprise")); // plan allow
        Assert.False(flag.IsEnabledFor(PlatformSettingsCatalog.FlagAiSuggest, null, null));                // platform-level: only at 100%
        Assert.False((flag with { Enabled = false }).IsEnabledFor(PlatformSettingsCatalog.FlagAiSuggest, outOfRollout, "enterprise"));

        var rollout = new FeatureFlagValue { Enabled = true, RolloutPercent = 50 };
        Assert.True(rollout.IsEnabledFor(PlatformSettingsCatalog.FlagAiSuggest, inRollout, null));
        Assert.False(rollout.IsEnabledFor(PlatformSettingsCatalog.FlagAiSuggest, outOfRollout, null));
        Assert.True(FeatureFlagValue.On().IsEnabledFor(PlatformSettingsCatalog.FlagAiSuggest, null, null));
    }

    // ── Reader ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Unset_uses_the_callers_fallback_then_the_registry_default()
    {
        var (_, reader, _) = Reader();
        Assert.Equal(45, await reader.GetInt32Async(PlatformSettingsCatalog.AccessTokenMinutes, 45));
        Assert.Equal(30, await reader.GetInt32Async(PlatformSettingsCatalog.AccessTokenMinutes));
        Assert.Equal(45, reader.GetInt32(PlatformSettingsCatalog.AccessTokenMinutes, 45));
    }

    [Fact]
    public async Task Workspace_override_beats_plan_beats_platform_and_only_where_the_scope_is_allowed()
    {
        var (source, reader, _) = Reader();
        var workspace = Guid.NewGuid();
        source.Set(PlatformSettingsCatalog.DocumentUploadMb, 20)
              .Set(PlatformSettingsCatalog.DocumentUploadMb, 50, PlatformSettingsRedisKeys.PlanHash("pro"))
              .Set(PlatformSettingsCatalog.DocumentUploadMb, 80, PlatformSettingsRedisKeys.WorkspaceHash(workspace))
              // Not allowed per workspace: must be ignored.
              .Set(PlatformSettingsCatalog.AccessTokenMinutes, 200, PlatformSettingsRedisKeys.WorkspaceHash(workspace));

        Assert.Equal(20, await reader.GetInt32Async(PlatformSettingsCatalog.DocumentUploadMb));
        Assert.Equal(50, await reader.GetInt32Async(PlatformSettingsCatalog.DocumentUploadMb, context: new SettingContext(Guid.NewGuid(), "pro")));
        Assert.Equal(80, await reader.GetInt32Async(PlatformSettingsCatalog.DocumentUploadMb, context: new SettingContext(workspace, "pro")));
        Assert.Equal(30, await reader.GetInt32Async(PlatformSettingsCatalog.AccessTokenMinutes, context: new SettingContext(workspace)));
    }

    [Fact]
    public async Task A_stored_value_that_fails_its_definition_is_ignored()
    {
        var (source, reader, _) = Reader();
        source.Set(PlatformSettingsCatalog.AccessTokenMinutes, 100000);
        Assert.Equal(30, await reader.GetInt32Async(PlatformSettingsCatalog.AccessTokenMinutes));
    }

    [Fact]
    public async Task Changes_arrive_after_the_ttl_without_a_restart()
    {
        var (source, reader, clock) = Reader(TimeSpan.FromSeconds(10));
        source.Set(PlatformSettingsCatalog.LockoutDurationMinutes, 20);
        Assert.Equal(20, await reader.GetInt32Async(PlatformSettingsCatalog.LockoutDurationMinutes));

        source.Set(PlatformSettingsCatalog.LockoutDurationMinutes, 40);
        Assert.Equal(20, await reader.GetInt32Async(PlatformSettingsCatalog.LockoutDurationMinutes)); // cached
        clock.Advance(TimeSpan.FromSeconds(11));
        Assert.Equal(40, await reader.GetInt32Async(PlatformSettingsCatalog.LockoutDurationMinutes));
    }

    [Fact]
    public async Task An_evicted_platform_hash_keeps_the_last_snapshot_instead_of_reverting_to_defaults()
    {
        var (source, reader, clock) = Reader(TimeSpan.FromSeconds(10));
        source.Set(PlatformSettingsCatalog.MaintenanceEnabled, true);
        Assert.True(await reader.GetBooleanAsync(PlatformSettingsCatalog.MaintenanceEnabled));

        source.Evict();
        clock.Advance(TimeSpan.FromSeconds(11));
        Assert.True(await reader.GetBooleanAsync(PlatformSettingsCatalog.MaintenanceEnabled));
    }

    [Fact]
    public async Task A_redis_failure_keeps_the_snapshot_and_never_throws()
    {
        var (source, reader, clock) = Reader(TimeSpan.FromSeconds(10));
        source.Set(PlatformSettingsCatalog.PasswordMinLength, 12);
        Assert.Equal(12, await reader.GetInt32Async(PlatformSettingsCatalog.PasswordMinLength));

        source.FailWith = new InvalidOperationException("redis down");
        clock.Advance(TimeSpan.FromSeconds(11));
        Assert.Equal(12, await reader.GetInt32Async(PlatformSettingsCatalog.PasswordMinLength));

        var (cold, coldReader, _) = Reader();
        cold.FailWith = new InvalidOperationException("redis down");
        Assert.Equal(8, await coldReader.GetInt32Async(PlatformSettingsCatalog.PasswordMinLength, 8));
    }

    [Fact]
    public async Task Sync_getters_answer_from_the_snapshot_and_refresh_it_in_the_background()
    {
        var (source, reader, _) = Reader(TimeSpan.Zero);
        source.Set(PlatformSettingsCatalog.LoginRateLimit, 9);

        // Nothing read yet: the fallback, and a refresh is started.
        Assert.Equal(5, reader.GetInt32(PlatformSettingsCatalog.LoginRateLimit, 5));
        await WaitUntilAsync(() => reader.GetInt32(PlatformSettingsCatalog.LoginRateLimit, 5) == 9);

        source.Set(PlatformSettingsCatalog.LoginRateLimit, 11);
        await WaitUntilAsync(() => reader.GetInt32(PlatformSettingsCatalog.LoginRateLimit, 5) == 11);
    }

    [Fact]
    public async Task Flags_read_through_the_reader_honour_the_callers_fallback_only_when_unset()
    {
        var (source, reader, _) = Reader();
        var workspace = Guid.Parse("3f2504e0-4f89-11d3-9a0c-0305e82c3301");
        Assert.False(await reader.IsEnabledAsync(PlatformSettingsCatalog.FlagAiSuggest, new SettingContext(workspace), fallback: false));
        Assert.True(await reader.IsEnabledAsync(PlatformSettingsCatalog.FlagAiSuggest, new SettingContext(workspace)));

        source.Set(PlatformSettingsCatalog.FlagAiSuggest, new FeatureFlagValue { Enabled = true, RolloutPercent = 20 });
        var fresh = new PlatformSettingsReader(source, NullLogger<PlatformSettingsReader>.Instance);
        Assert.True(await fresh.IsEnabledAsync(PlatformSettingsCatalog.FlagAiSuggest, new SettingContext(workspace), fallback: false));
        Assert.False(await fresh.IsEnabledAsync(PlatformSettingsCatalog.FlagAiSuggest, new SettingContext(Guid.Parse("00000000-0000-0000-0000-000000000001"))));
    }

    // ── Fixture ─────────────────────────────────────────────────────────────────────────────

    internal static (InMemoryPlatformSettingsSource Source, PlatformSettingsReader Reader, ManualClock Clock) Reader(TimeSpan? ttl = null)
    {
        var source = new InMemoryPlatformSettingsSource();
        var clock = new ManualClock();
        return (source, new PlatformSettingsReader(source, NullLogger<PlatformSettingsReader>.Instance, clock, ttl ?? TimeSpan.FromSeconds(10)), clock);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "warptalk-backend.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Could not find the backend repository root.");
    }

    internal sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }
}
