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

    // Denied, not permitted. A workspace whose policy could not be read is not a workspace that
    // permits everything, and failing open here would turn any workspace-service outage into a
    // product-wide lifting of plugin restrictions. One call, two readings: this one folds Unknown
    // into "no", ReadAllowAnyPluginsAsync keeps it apart.
    public async Task<bool> AllowsPluginUsageAsync(Guid workspaceId, CancellationToken ct = default) =>
        await ReadAllowAnyPluginsAsync(workspaceId, ct) == WorkspacePluginPolicyAnswer.Allowed;

    public async Task<WorkspacePluginPolicyAnswer> ReadAllowAnyPluginsAsync(Guid workspaceId, CancellationToken ct = default)
    {
        try
        {
            var response = await _workspaceClient.GetWorkspaceSettingsAsync(
                new GetWorkspaceSettingsRequest { WorkspaceId = workspaceId.ToString() },
                cancellationToken: ct);

            return response.AllowAnyPlugins
                ? WorkspacePluginPolicyAnswer.Allowed
                : WorkspacePluginPolicyAnswer.NotAllowed;
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            // Unknown rather than "off". Every caller that reaches here has already had the same
            // workspace service confirm the caller's membership of this workspace, so "no such
            // workspace" is the service contradicting itself, not a fact worth writing down.
            _logger.LogWarning(ex, "Workspace {WorkspaceId} was not found while checking plugin policy.", workspaceId);
            return WorkspacePluginPolicyAnswer.Unknown;
        }
        catch (RpcException ex)
        {
            _logger.LogWarning(ex, "Workspace plugin policy check failed for workspace {WorkspaceId}.", workspaceId);
            return WorkspacePluginPolicyAnswer.Unknown;
        }
    }
}
