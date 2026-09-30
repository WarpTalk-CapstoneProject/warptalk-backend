using System.Text.Json;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.AssistantService.Domain.Entities;

namespace WarpTalk.AssistantService.Application.Helpers;

/// <summary>
/// What the platform says about one marketplace plugin in one workspace, and which layer said it.
/// </summary>
/// <param name="Allowed">Whether the workspace may have the plugin at all.</param>
/// <param name="Source">A <see cref="PluginWorkspaceAccessConstants.Source"/> value.</param>
/// <param name="OverrideState">The override's state when there is one, whatever decided.</param>
/// <param name="Reason">The override's reason, when an override decided.</param>
public sealed record PluginWorkspaceVerdict(
    bool Allowed,
    string Source,
    string? OverrideState = null,
    string? Reason = null);

/// <summary>
/// The platform admin's rule, in one place: retired &gt; override &gt; plan rule &gt; default.
/// </summary>
/// <remarks>
/// Pure, so the guard (every tool call, every catalog listing), the Owner's page and the admin's
/// "Workspaces" tab all resolve it the same way and cannot disagree about a workspace. The
/// workspace Owner's own list is applied after this, by <see cref="WorkspacePluginAvailability"/>:
/// the platform decides what an Owner MAY add, the Owner decides what the workspace HAS.
/// </remarks>
public static class PluginWorkspaceAccess
{
    public static PluginWorkspaceVerdict Resolve(
        Plugin plugin,
        string? workspacePlanSlug,
        WorkspacePluginOverride? workspaceOverride)
    {
        // A private plugin belongs to one workspace and is not in the marketplace; the platform
        // layer has nothing to say about it.
        if (plugin.OwnerWorkspaceId is not null)
            return new PluginWorkspaceVerdict(true, PluginWorkspaceAccessConstants.Source.Private);

        // Retired is retired everywhere. An override that enabled it stays stored, so reinstating
        // the plugin restores the workspace's state as it was, but it cannot un-retire it.
        if (!plugin.IsActive)
            return new PluginWorkspaceVerdict(
                false,
                PluginWorkspaceAccessConstants.Source.Retired,
                workspaceOverride?.State);

        if (workspaceOverride is not null)
            return new PluginWorkspaceVerdict(
                workspaceOverride.State == PluginWorkspaceAccessConstants.OverrideState.Enabled,
                PluginWorkspaceAccessConstants.Source.Override,
                workspaceOverride.State,
                workspaceOverride.Reason);

        // Anything but 'available' is closed: the column's CHECK allows only the two values, and
        // an unreadable default must not read as "every workspace".
        if (!string.Equals(plugin.WorkspaceDefault, PluginWorkspaceAccessConstants.Default.Available, StringComparison.Ordinal))
            return new PluginWorkspaceVerdict(false, PluginWorkspaceAccessConstants.Source.Default);

        var plans = AllowedPlans(plugin);
        if (plans is not null && !MatchesPlan(plans, workspacePlanSlug))
            return new PluginWorkspaceVerdict(false, PluginWorkspaceAccessConstants.Source.Plan);

        return new PluginWorkspaceVerdict(true, PluginWorkspaceAccessConstants.Source.Default);
    }

    /// <summary>
    /// The plan rule: null means every plan. A value that will not parse is read as a rule nobody
    /// matches - closed, like an unreadable default - rather than as no rule.
    /// </summary>
    public static IReadOnlyList<string>? AllowedPlans(Plugin plugin)
    {
        if (string.IsNullOrWhiteSpace(plugin.AllowedPlanSlugsJson)) return null;

        try
        {
            var plans = JsonSerializer.Deserialize<List<string>>(plugin.AllowedPlanSlugsJson) ?? [];
            var normalized = Normalize(plans);
            // The write path stores null for "every plan" and never an empty array; one that got
            // there anyway is read the same way it was meant.
            return normalized.Count == 0 ? null : normalized;
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>Trimmed, lower-cased, de-duplicated, blanks dropped.</summary>
    public static IReadOnlyList<string> Normalize(IEnumerable<string?> plans) =>
        plans
            .Select(plan => plan?.Trim().ToLowerInvariant() ?? string.Empty)
            .Where(plan => plan.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>A workspace with no plan never matches a plan rule.</summary>
    public static bool MatchesPlan(IReadOnlyList<string> plans, string? workspacePlanSlug)
    {
        var plan = workspacePlanSlug?.Trim().ToLowerInvariant();
        return !string.IsNullOrEmpty(plan) && plans.Contains(plan, StringComparer.Ordinal);
    }

    /// <summary>The value the admin picked, reconstructed from the two columns it is stored in.</summary>
    public static string DefaultOf(Plugin plugin) =>
        !plugin.IsActive
            ? PluginWorkspaceAccessConstants.Default.Retired
            : string.Equals(plugin.WorkspaceDefault, PluginWorkspaceAccessConstants.Default.Available, StringComparison.Ordinal)
                ? PluginWorkspaceAccessConstants.Default.Available
                : PluginWorkspaceAccessConstants.Default.OptIn;
}
