using WarpTalk.AssistantService.Domain.Entities;

namespace WarpTalk.AssistantService.Domain.Interfaces;

public interface IPluginRequestRepository : IGenericRepository<PluginRequest>
{
    /// <summary>One workspace's requests in a status, oldest first.</summary>
    Task<IReadOnlyList<PluginRequest>> ListForWorkspaceAsync(
        Guid workspaceId,
        string status,
        CancellationToken ct = default);
}
