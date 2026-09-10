using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.Shared;

namespace WarpTalk.AssistantService.Application.Services;

/// <summary>
/// Applies a workspace's plugin policy. WT-646.
/// </summary>
/// <remarks>
/// Every gate a plugin has to pass - catalog, install, connect, tool list, execute - goes through
/// here, so the no-workspace rule and the refusal's wording are written once.
/// </remarks>
public class WorkspacePluginGuard : IWorkspacePluginGuard
{
    private readonly IWorkspacePluginPolicyClient _policyClient;
    private readonly IWorkspaceMembershipClient _membershipClient;

    public WorkspacePluginGuard(
        IWorkspacePluginPolicyClient policyClient,
        IWorkspaceMembershipClient membershipClient)
    {
        _policyClient = policyClient;
        _membershipClient = membershipClient;
    }

    public async Task<Result> CanUsePluginsAsync(Guid? workspaceId, CancellationToken ct = default)
    {
        if (!workspaceId.HasValue) return Result.Success();

        return await ApplyPolicyAsync(workspaceId.Value, ct);
    }

    public async Task<Result> CanUsePluginsInWorkspaceAsync(
        Guid? workspaceId,
        Guid userId,
        CancellationToken ct = default)
    {
        if (!workspaceId.HasValue)
            return Result.Failure(
                PluginConstants.WorkspacePolicyMessages.WorkspaceRequired,
                PluginConstants.ErrorCodes.PermissionDenied);

        // Membership before policy, and not only for tidiness: asking the policy first would let a
        // caller probe whether an arbitrary workspace permits plugins by watching which refusal
        // comes back. A non-member gets the same answer whatever that workspace has configured.
        var membership = await _membershipClient.GetMembershipAsync(workspaceId.Value, userId, ct);
        if (!membership.IsMember || !membership.IsActive)
            return Result.Failure(
                PluginConstants.WorkspacePolicyMessages.NotAWorkspaceMember,
                PluginConstants.ErrorCodes.PermissionDenied);

        return await ApplyPolicyAsync(workspaceId.Value, ct);
    }

    private async Task<Result> ApplyPolicyAsync(Guid workspaceId, CancellationToken ct) =>
        await _policyClient.AllowsPluginUsageAsync(workspaceId, ct)
            ? Result.Success()
            : Result.Failure(
                PluginConstants.WorkspacePolicyMessages.PluginsDisabled,
                PluginConstants.ErrorCodes.PermissionDenied);
}
