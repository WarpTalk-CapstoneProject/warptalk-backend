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

    public WorkspacePluginGuard(IWorkspacePluginPolicyClient policyClient)
    {
        _policyClient = policyClient;
    }

    public async Task<Result> CanUsePluginsAsync(Guid? workspaceId, CancellationToken ct = default)
    {
        if (!workspaceId.HasValue) return Result.Success();

        return await _policyClient.AllowsPluginUsageAsync(workspaceId.Value, ct)
            ? Result.Success()
            : Result.Failure(
                PluginConstants.WorkspacePolicyMessages.PluginsDisabled,
                PluginConstants.ErrorCodes.PermissionDenied);
    }
}
