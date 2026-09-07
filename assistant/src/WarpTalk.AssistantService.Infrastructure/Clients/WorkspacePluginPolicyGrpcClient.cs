using Grpc.Core;
using Microsoft.Extensions.Logging;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.Shared.Protos;

namespace WarpTalk.AssistantService.Infrastructure.Clients;

public class WorkspacePluginPolicyGrpcClient : IWorkspacePluginPolicyClient
{
    private readonly WorkspaceService.WorkspaceServiceClient _workspaceClient;
    private readonly ILogger<WorkspacePluginPolicyGrpcClient> _logger;

    public WorkspacePluginPolicyGrpcClient(
        WorkspaceService.WorkspaceServiceClient workspaceClient,
        ILogger<WorkspacePluginPolicyGrpcClient> logger)
    {
        _workspaceClient = workspaceClient;
        _logger = logger;
    }

    public async Task<bool> AllowsPluginUsageAsync(Guid workspaceId, CancellationToken ct = default)
    {
        var policy = await GetPluginPolicyAsync(workspaceId, ct);

        // Deliberately still the AllowAnyPlugins answer alone, unchanged by WT-646. This method has
        // callers (McpToolOrchestrator) whose behaviour must not shift because a workspace now also
        // carries an allowlist; applying the allowlist is B5's job and needs a plugin key to apply
        // it to, which this signature does not have.
        return policy.AllowAnyPlugins;
    }

    public async Task<WorkspacePluginPolicySnapshot> GetPluginPolicyAsync(Guid workspaceId, CancellationToken ct = default)
    {
        try
        {
            var response = await _workspaceClient.GetWorkspaceSettingsAsync(
                new GetWorkspaceSettingsRequest { WorkspaceId = workspaceId.ToString() },
                cancellationToken: ct);

            // PluginPolicy is absent when the workspace service predates WT-646. That is not a
            // workspace with an empty policy — it is a workspace that has no opinion beyond
            // AllowAnyPlugins, and the defaults below are chosen so it behaves exactly as it did
            // before this field existed: no allowlist, members may install, no approval gate.
            // Reading the message's zero values instead would tell every caller that a
            // perfectly ordinary workspace forbids member installs.
            var pluginPolicy = response.PluginPolicy;
            if (pluginPolicy is null)
            {
                return new WorkspacePluginPolicySnapshot(
                    AllowAnyPlugins: response.AllowAnyPlugins,
                    AllowedPluginKeys: null,
                    AllowMemberPluginInstall: true,
                    RequirePluginApproval: false);
            }

            return new WorkspacePluginPolicySnapshot(
                AllowAnyPlugins: response.AllowAnyPlugins,
                // Null, not an empty list, when no allowlist is configured — the two mean opposite
                // things downstream and allowlist_enforced is the only thing that separates them,
                // proto3 repeated fields having no presence of their own.
                AllowedPluginKeys: pluginPolicy.AllowlistEnforced
                    ? pluginPolicy.AllowedPluginKeys.ToList()
                    : null,
                AllowMemberPluginInstall: pluginPolicy.AllowMemberPluginInstall,
                RequirePluginApproval: pluginPolicy.RequirePluginApproval);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            _logger.LogWarning(ex, "Workspace {WorkspaceId} was not found while checking plugin policy.", workspaceId);
            return WorkspacePluginPolicySnapshot.Denied;
        }
        catch (RpcException ex)
        {
            _logger.LogWarning(ex, "Workspace plugin policy check failed for workspace {WorkspaceId}.", workspaceId);
            return WorkspacePluginPolicySnapshot.Denied;
        }
    }
}
