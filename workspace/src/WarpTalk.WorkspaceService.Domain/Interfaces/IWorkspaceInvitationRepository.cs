using WarpTalk.WorkspaceService.Domain.Entities;

namespace WarpTalk.WorkspaceService.Domain.Interfaces;

public interface IWorkspaceInvitationRepository : IGenericRepository<WorkspaceInvitation>
{
    Task<WorkspaceInvitation?> GetByTokenHashAsync(string tokenHash, CancellationToken ct = default);
    Task<WorkspaceInvitation?> GetPendingByEmailAsync(Guid workspaceId, string email, CancellationToken ct = default);
    Task<(List<WorkspaceInvitation> Items, int TotalCount)> GetInvitationsByWorkspaceAsync(Guid workspaceId, int page, int pageSize, CancellationToken ct = default);
}
