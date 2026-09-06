namespace WarpTalk.AssistantService.Application.Interfaces;

public interface IWorkspacePluginPolicyClient
{
    Task<bool> AllowsPluginUsageAsync(Guid workspaceId, CancellationToken ct = default);
}
