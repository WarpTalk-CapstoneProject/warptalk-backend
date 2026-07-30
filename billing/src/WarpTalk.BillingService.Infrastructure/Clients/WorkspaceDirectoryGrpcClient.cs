using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.Shared.Protos;

namespace WarpTalk.BillingService.Infrastructure.Clients;

public sealed class WorkspaceDirectoryGrpcClient(
    WorkspaceService.WorkspaceServiceClient client) : IWorkspaceDirectory
{
    public async Task<IReadOnlyDictionary<Guid, string>> GetNamesAsync(
        IEnumerable<Guid> workspaceIds,
        CancellationToken cancellationToken = default)
    {
        var request = new GetWorkspaceNamesRequest();
        request.WorkspaceIds.AddRange(workspaceIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .Select(id => id.ToString()));
        if (request.WorkspaceIds.Count == 0)
            return new Dictionary<Guid, string>();

        var response = await client.GetWorkspaceNamesAsync(
            request,
            cancellationToken: cancellationToken);
        return response.Workspaces
            .Where(item => Guid.TryParse(item.WorkspaceId, out _))
            .ToDictionary(
                item => Guid.Parse(item.WorkspaceId),
                item => item.WorkspaceName);
    }
}
