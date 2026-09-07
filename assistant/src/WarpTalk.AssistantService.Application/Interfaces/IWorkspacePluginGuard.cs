using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.Shared;

namespace WarpTalk.AssistantService.Application.Interfaces;

/// <summary>
/// A workspace's plugin policy resolved once and then applied to as many plugin keys as the caller
/// needs. WT-646.
/// </summary>
/// <remarks>
/// The catalog and the tool list both judge every plugin a user has; asking the workspace service
/// per key would be one gRPC round trip per row. Resolving the policy into this value first keeps
/// that at one call per request, and keeps the null-versus-empty allowlist rule in exactly one
/// place rather than repeated at four call sites.
/// </remarks>
public sealed class WorkspacePluginGate
{
    private readonly WorkspacePluginPolicySnapshot? _policy;

    private WorkspacePluginGate(WorkspacePluginPolicySnapshot? policy) => _policy = policy;

    /// <summary>
    /// No workspace in play, so no workspace policy applies.
    /// </summary>
    /// <remarks>
    /// This is the case for every personal call - the plugins settings page, an install, a
    /// connect - made outside a workspace context, and it must permit everything. Workspace
    /// policy governs what a workspace's members may do IN that workspace; a request that names
    /// no workspace has none to apply, and denying it would take away access that works today.
    /// </remarks>
    public static WorkspacePluginGate Unscoped { get; } = new(null);

    internal static WorkspacePluginGate For(WorkspacePluginPolicySnapshot policy) => new(policy);

    /// <summary>
    /// The resolved policy, or null when no workspace was in play. Exposed for the one caller that
    /// needs more than a yes/no - the install path, which also has to know whether a plain Member
    /// may install before it spends a round trip resolving the caller's role.
    /// </summary>
    public WorkspacePluginPolicySnapshot? Policy => _policy;

    /// <summary>
    /// Whether this workspace permits <paramref name="pluginKey"/> to be installed, connected or
    /// invoked at all.
    /// </summary>
    public Result Permits(string pluginKey)
    {
        if (_policy is null) return Result.Success();

        var allowlist = _policy.AllowedPluginKeys;

        // NULL IS NOT EMPTY. Null means the workspace configured no allowlist - which is every
        // workspace that predates WT-646 - and the answer falls back to the original on/off
        // switch, exactly as it did before an allowlist existed. An empty list is a deliberate
        // allowlist that permits nothing. Collapsing the two with `?? []` here would deny plugins
        // across the entire product on the day this shipped.
        if (allowlist is null)
        {
            return _policy.AllowAnyPlugins
                ? Result.Success()
                : Result.Failure(
                    PluginConstants.WorkspacePolicyMessages.PluginsDisabled,
                    PluginConstants.ErrorCodes.PermissionDenied);
        }

        // A configured allowlist supersedes AllowAnyPlugins rather than being ANDed with it: the
        // contract calls AllowAnyPlugins "the whole answer when AllowedPluginKeys is null", which
        // it can only be if it stops being the answer when a list is present. So an allowlist is
        // how a workspace with plugins switched off turns a chosen few back on, and an empty
        // allowlist denies just as thoroughly as AllowAnyPlugins=false did.
        return allowlist.Contains(pluginKey, StringComparer.Ordinal)
            ? Result.Success()
            : Result.Failure(
                PluginConstants.WorkspacePolicyMessages.NotOnAllowlist,
                PluginConstants.ErrorCodes.WorkspacePluginNotAllowed);
    }

    /// <summary>
    /// Whether an ordinary Member may install here without the caller's role being resolved.
    /// False means the install path has to ask who the caller is.
    /// </summary>
    public bool PermitsMemberInstall => _policy is null || _policy.AllowMemberPluginInstall;

    /// <summary>
    /// Whether no plugin key whatsoever can pass <see cref="Permits"/>, so a caller enumerating
    /// plugins can answer "none" without loading any.
    /// </summary>
    /// <remarks>
    /// Both refusals that do not depend on the key land here: plugins switched off with no
    /// allowlist, and an allowlist that lists nothing. The second is the reason null and empty
    /// have to stay distinguishable - a null allowlist read as empty would put every workspace in
    /// the product into this state.
    /// </remarks>
    public bool PermitsNothing =>
        _policy is not null
        && (_policy.AllowedPluginKeys is null
            ? !_policy.AllowAnyPlugins
            : _policy.AllowedPluginKeys.Count == 0);
}

public interface IWorkspacePluginGuard
{
    /// <summary>
    /// Resolves the policy for one request. A null <paramref name="workspaceId"/> yields
    /// <see cref="WorkspacePluginGate.Unscoped"/> without any round trip.
    /// </summary>
    Task<WorkspacePluginGate> ResolveAsync(Guid? workspaceId, CancellationToken ct = default);

    /// <summary>
    /// Whether this plugin may be used in this workspace - the one-key convenience over
    /// <see cref="ResolveAsync"/>.
    /// </summary>
    Task<Result> CanUseAsync(Guid? workspaceId, string pluginKey, CancellationToken ct = default);

    /// <summary>
    /// Whether this user may install this plugin in this workspace: the allowlist first, then
    /// <c>allow_member_plugin_install</c>, which is resolved against the caller's workspace role
    /// and only when the workspace has actually turned member installs off.
    /// </summary>
    Task<Result> CanInstallAsync(Guid? workspaceId, Guid userId, string pluginKey, CancellationToken ct = default);
}
