// =======================================================
// IWorkspaceQuotaSnapshotRepository.cs
// =======================================================

using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Domain.Interfaces;

public interface IWorkspaceQuotaSnapshotRepository
{
    Task<WorkspaceQuotaSnapshot?> GetByWorkspaceIdAsync(
        Guid workspaceId,
        CancellationToken cancellationToken = default);

    Task AddAsync(
        WorkspaceQuotaSnapshot snapshot,
        CancellationToken cancellationToken = default);

    Task UpdateAsync(
        WorkspaceQuotaSnapshot snapshot,
        CancellationToken cancellationToken = default);
}