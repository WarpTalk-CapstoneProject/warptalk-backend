using WarpTalk.AssistantService.Application.Helpers;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.AssistantService.Domain.Entities;

namespace WarpTalk.AssistantService.Tests.Plugins;

/// <summary>
/// The platform layer's effective state: retired &gt; override &gt; plan rule &gt; default. Every
/// surface - the guard, the Owner's page, both admin tabs - resolves through this one function.
/// </summary>
public class PluginWorkspaceAccessTests
{
    private const string Enabled = PluginWorkspaceAccessConstants.OverrideState.Enabled;
    private const string Disabled = PluginWorkspaceAccessConstants.OverrideState.Disabled;

    [Theory]
    // default      plans                    workspace plan  override   -> allowed  source
    [InlineData("available", null, "free", null, true, "default")]
    [InlineData("available", null, null, null, true, "default")]
    [InlineData("opt_in", null, "enterprise", null, false, "default")]
    [InlineData("opt_in", null, "enterprise", Enabled, true, "override")]
    [InlineData("available", null, "free", Disabled, false, "override")]
    [InlineData("available", "business,enterprise", "business", null, true, "default")]
    [InlineData("available", "business,enterprise", "ENTERPRISE", null, true, "default")]
    [InlineData("available", "business,enterprise", "free", null, false, "plan")]
    // A workspace with no plan (or one the workspace service could not report) matches no rule.
    [InlineData("available", "business,enterprise", null, null, false, "plan")]
    // An override beats the plan rule both ways.
    [InlineData("available", "business,enterprise", "free", Enabled, true, "override")]
    [InlineData("available", "business,enterprise", "business", Disabled, false, "override")]
    // Opt-in with a plan rule: still hidden until enabled; the rule only narrows "available".
    [InlineData("opt_in", "business", "business", null, false, "default")]
    public void Resolve_AppliesTheLayersInOrder(
        string workspaceDefault,
        string? plans,
        string? workspacePlan,
        string? overrideState,
        bool allowed,
        string source)
    {
        var plugin = WorkspacePluginGuardTests.Marketplace("linear");
        plugin.WorkspaceDefault = workspaceDefault;
        plugin.AllowedPlanSlugsJson = plans is null
            ? null
            : "[" + string.Join(",", plans.Split(',').Select(p => $"\"{p}\"")) + "]";

        var verdict = PluginWorkspaceAccess.Resolve(plugin, workspacePlan, Override(plugin, overrideState));

        Assert.Equal(allowed, verdict.Allowed);
        Assert.Equal(source, verdict.Source);
    }

    [Fact]
    public void ARetiredPlugin_IsOffEverywhere_AndAnEnablingOverrideCannotBringItBack()
    {
        var plugin = WorkspacePluginGuardTests.Marketplace("linear");
        plugin.IsActive = false;

        var verdict = PluginWorkspaceAccess.Resolve(plugin, "enterprise", Override(plugin, Enabled));

        Assert.False(verdict.Allowed);
        Assert.Equal(PluginWorkspaceAccessConstants.Source.Retired, verdict.Source);
        // Kept, so reinstating the plugin restores the workspace's state as it was.
        Assert.Equal(Enabled, verdict.OverrideState);
    }

    [Fact]
    public void APrivatePlugin_IsNotGovernedByThePlatform()
    {
        var plugin = WorkspacePluginGuardTests.Private("ws_crm", Guid.NewGuid());
        plugin.WorkspaceDefault = PluginWorkspaceAccessConstants.Default.OptIn;

        var verdict = PluginWorkspaceAccess.Resolve(plugin, null, null);

        Assert.True(verdict.Allowed);
        Assert.Equal(PluginWorkspaceAccessConstants.Source.Private, verdict.Source);
    }

    [Fact]
    public void TheOverridesReason_TravelsWithTheVerdict()
    {
        var plugin = WorkspacePluginGuardTests.Marketplace("linear");

        var verdict = PluginWorkspaceAccess.Resolve(plugin, null, Override(plugin, Disabled, "data residency"));

        Assert.Equal("data residency", verdict.Reason);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"plans\":1}")]
    public void AnUnreadablePlanRule_IsClosed_NotOpen(string stored)
    {
        var plugin = WorkspacePluginGuardTests.Marketplace("linear");
        plugin.AllowedPlanSlugsJson = stored;

        var verdict = PluginWorkspaceAccess.Resolve(plugin, "enterprise", null);

        Assert.False(verdict.Allowed);
        Assert.Equal(PluginWorkspaceAccessConstants.Source.Plan, verdict.Source);
    }

    [Fact]
    public void AnUnknownDefault_IsClosed_NotOpen()
    {
        var plugin = WorkspacePluginGuardTests.Marketplace("linear");
        plugin.WorkspaceDefault = "everyone-please";

        Assert.False(PluginWorkspaceAccess.Resolve(plugin, "enterprise", null).Allowed);
    }

    [Fact]
    public void DefaultOf_ReadsRetiredFromIsActive_AndTheRestFromTheColumn()
    {
        var plugin = WorkspacePluginGuardTests.Marketplace("linear");
        Assert.Equal("available", PluginWorkspaceAccess.DefaultOf(plugin));

        plugin.WorkspaceDefault = PluginWorkspaceAccessConstants.Default.OptIn;
        Assert.Equal("opt_in", PluginWorkspaceAccess.DefaultOf(plugin));

        plugin.IsActive = false;
        Assert.Equal("retired", PluginWorkspaceAccess.DefaultOf(plugin));
    }

    private static WorkspacePluginOverride? Override(Plugin plugin, string? state, string? reason = null) =>
        state is null
            ? null
            : new WorkspacePluginOverride
            {
                Id = Guid.NewGuid(),
                WorkspaceId = Guid.NewGuid(),
                PluginId = plugin.Id,
                State = state,
                Reason = reason,
                SetAt = DateTime.UtcNow,
            };
}
