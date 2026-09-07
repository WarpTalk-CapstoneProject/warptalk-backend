using Microsoft.Extensions.Logging;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.Shared;

namespace WarpTalk.AssistantService.Application.Services;

/// <summary>
/// Applies a workspace's plugin policy. WT-646 B5.
/// </summary>
/// <remarks>
/// B4 made the policy readable; this is the only thing that acts on it. Every gate a plugin has to
/// pass - catalog, install, connect, execute - goes through here, so the null-versus-empty
/// allowlist rule and the Owner/Admin install rule are written once.
/// </remarks>
public class WorkspacePluginGuard : IWorkspacePluginGuard
{
    private readonly IWorkspacePluginPolicyClient _policyClient;
    private readonly IWorkspaceMembershipClient _membershipClient;
    private readonly ILogger<WorkspacePluginGuard> _logger;

    public WorkspacePluginGuard(
        IWorkspacePluginPolicyClient policyClient,
        IWorkspaceMembershipClient membershipClient,
        ILogger<WorkspacePluginGuard> logger)
    {
        _policyClient = policyClient;
        _membershipClient = membershipClient;
        _logger = logger;
    }

    public async Task<WorkspacePluginGate> ResolveAsync(Guid? workspaceId, CancellationToken ct = default)
    {
        if (!workspaceId.HasValue) return WorkspacePluginGate.Unscoped;

        var policy = await _policyClient.GetPluginPolicyAsync(workspaceId.Value, ct);
        WarnIfApprovalIsUnenforceable(workspaceId.Value, policy);
        return WorkspacePluginGate.For(policy);
    }

    public async Task<Result> CanUseAsync(Guid? workspaceId, string pluginKey, CancellationToken ct = default)
    {
        var gate = await ResolveAsync(workspaceId, ct);
        return gate.Permits(pluginKey);
    }

    public async Task<Result> CanInstallAsync(
        Guid? workspaceId,
        Guid userId,
        string pluginKey,
        CancellationToken ct = default)
    {
        var gate = await ResolveAsync(workspaceId, ct);

        var permitted = gate.Permits(pluginKey);
        if (!permitted.IsSuccess) return permitted;

        // The role lookup is a second round trip, so it only happens for the workspaces that
        // actually restrict installation. The common case - and every workspace that predates
        // WT-646, which reports AllowMemberPluginInstall true - costs nothing extra.
        if (gate.PermitsMemberInstall) return Result.Success();

        var membership = await _membershipClient.GetMembershipAsync(workspaceId!.Value, userId, ct);
        return membership.IsOwnerOrAdmin
            ? Result.Success()
            : Result.Failure(
                PluginConstants.WorkspacePolicyMessages.InstallRequiresAdmin,
                PluginConstants.ErrorCodes.WorkspaceInstallRequiresAdmin);
    }

    /// <summary>
    /// <c>require_plugin_approval</c> has nothing to enforce against unless the workspace also runs
    /// an allowlist, so a workspace that turned it on alone gets a log line rather than a refusal.
    /// </summary>
    /// <remarks>
    /// There is no approval store in this service: <c>plugin_installations</c> has installed,
    /// disabled and not-installed and no fourth state, no reviewer column, no queue, and nothing
    /// notifies an Owner that a request is waiting. With an allowlist configured, the allowlist IS
    /// the approval record - an admin adding a key is the approval, and <see cref="WorkspacePluginGate.Permits"/>
    /// already refuses everything not on it - so the flag adds no separate check there.
    /// <para>
    /// Without one, the flag asks for a gate no user could ever pass and no admin could ever open.
    /// Denying on it would take plugins away from a workspace with no route to giving them back;
    /// half-implementing an approval queue with no notification and no reviewer screen would be
    /// worse than the gap. So it is recorded here, honestly, and left to the ticket that builds
    /// the queue.
    /// </para>
    /// </remarks>
    private void WarnIfApprovalIsUnenforceable(Guid workspaceId, WorkspacePluginPolicySnapshot policy)
    {
        if (!policy.RequirePluginApproval || policy.AllowedPluginKeys is not null) return;

        _logger.LogWarning(
            "Workspace {WorkspaceId} requires plugin approval but configures no allowlist. There is no "
                + "approval store in the assistant service, so nothing is being enforced for it; configure "
                + "an allowlist, which is the only approval record this model can express.",
            workspaceId);
    }
}
