using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Application.Services.Interface
{
    public interface IWorkspaceQuotaSnapshotService
    {
        Task<WorkspaceQuotaSnapshot?> GetByWorkspaceIdAsync(
            Guid workspaceId,
            CancellationToken ct = default);

        Task<WorkspaceQuotaSnapshot> GetOrCreateAsync(
            Guid workspaceId,
            Guid planId,
            CancellationToken ct = default);

        Task UpdateSnapshotAsync(
            WorkspaceQuotaSnapshot snapshot,
            CancellationToken ct = default);
    }
}
