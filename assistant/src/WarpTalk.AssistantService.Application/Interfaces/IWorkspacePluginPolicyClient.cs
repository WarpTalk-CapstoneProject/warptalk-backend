namespace WarpTalk.AssistantService.Application.Interfaces;

/// <summary>
/// A workspace's plugin policy as the workspace service reports it. WT-646.
/// </summary>
/// <param name="AllowAnyPlugins">
/// The original workspace-wide on/off switch. Still the whole answer when
/// <paramref name="AllowedPluginKeys"/> is null.
/// </param>
/// <param name="AllowedPluginKeys">
/// The permitted plugin keys, or NULL when the workspace has configured no allowlist at all.
///
/// NULL AND EMPTY MEAN OPPOSITE THINGS. Null is "no allowlist — fall back to
/// <paramref name="AllowAnyPlugins"/>", and it is what every workspace that predates WT-646
/// reports, so treating it as "permit nothing" would deny plugins across the entire product.
/// An empty list is a deliberate allowlist permitting nothing. The gRPC contract carries the
/// distinction explicitly (WorkspacePluginPolicy.allowlist_enforced) because a proto3 repeated
/// field cannot.
///
/// The workspace service does NOT check these keys against the plugin catalog — it cannot, the
/// catalog is this service's own table. A key here may match no installed plugin.
/// </param>
/// <param name="AllowMemberPluginInstall">
/// Whether a plain Member may install a plugin for themselves. False confines installation to
/// Owner and Admin.
/// </param>
/// <param name="RequirePluginApproval">
/// Whether an installed plugin needs Owner/Admin approval before it may be invoked.
/// </param>
public record WorkspacePluginPolicySnapshot(
    bool AllowAnyPlugins,
    IReadOnlyList<string>? AllowedPluginKeys,
    bool AllowMemberPluginInstall,
    bool RequirePluginApproval)
{
    /// <summary>
    /// What to assume when the workspace service cannot be reached or does not know the workspace.
    /// Denies usage, and leaves the rest at the values that do not add a second refusal on top of
    /// the first — there is no point reporting "approval required" about a workspace whose policy
    /// could not be read at all.
    /// </summary>
    public static WorkspacePluginPolicySnapshot Denied { get; } =
        new(AllowAnyPlugins: false, AllowedPluginKeys: null, AllowMemberPluginInstall: false, RequirePluginApproval: false);
}

public interface IWorkspacePluginPolicyClient
{
    Task<bool> AllowsPluginUsageAsync(Guid workspaceId, CancellationToken ct = default);

    /// <summary>
    /// The full policy, for callers that need more than the workspace-wide on/off answer
    /// <see cref="AllowsPluginUsageAsync"/> gives. WT-646 B4 makes this available; enforcement
    /// against it is B5's job, so nothing calls it yet.
    /// </summary>
    Task<WorkspacePluginPolicySnapshot> GetPluginPolicyAsync(Guid workspaceId, CancellationToken ct = default);
}
