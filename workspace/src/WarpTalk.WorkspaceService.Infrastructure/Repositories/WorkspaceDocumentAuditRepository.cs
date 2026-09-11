using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Interfaces;
using WarpTalk.WorkspaceService.Infrastructure.Persistence;

namespace WarpTalk.WorkspaceService.Infrastructure.Repositories;

public class WorkspaceDocumentAuditRepository : GenericRepository<WorkspaceDocumentAudit>, IWorkspaceDocumentAuditRepository
{
    public WorkspaceDocumentAuditRepository(WorkspaceDbContext context) : base(context)
    {
    }

    public async Task<(List<WorkspaceDocumentAudit> Items, int TotalCount)> GetPagedAuditsAsync(
        Guid documentId,
        int page,
        int pageSize,
        bool isDescending = true,
        IReadOnlyCollection<string>? excludeActions = null,
        CancellationToken ct = default)
    {
        var query = _dbSet.AsNoTracking().Where(a => a.DocumentId == documentId);

        // IN THE QUERY, not after paging. Filtering the returned page instead would leave
        // TotalCount counting rows the caller never sees, so the last page would be short and the
        // page count wrong — and every document detail view writes a GetDocumentDetails row, so
        // for a much-read document that is most of the table.
        if (excludeActions is { Count: > 0 })
        {
            query = query.Where(a => !excludeActions.Contains(a.Action));
        }

        var totalCount = await query.CountAsync(ct);

        query = isDescending 
            ? query.OrderByDescending(a => a.ActionAt) 
            : query.OrderBy(a => a.ActionAt);

        var skip = Math.Max(0, (page - 1) * pageSize);
        var items = await query.Skip(skip).Take(pageSize).ToListAsync(ct);

        return (items, totalCount);
    }

    public Task<WorkspaceDocumentAudit?> GetLatestActionAsync(
        Guid documentId,
        string action,
        CancellationToken ct = default)
    {
        // ORDERED, unlike the FirstOrDefaultAsync calls this sits beside. A document can be
        // rejected, re-uploaded and rejected again, so "the rejection reason" is specifically the
        // most recent one — an unordered first row would show the uploader feedback they have
        // already answered.
        return _dbSet.AsNoTracking()
            .Where(a => a.DocumentId == documentId && a.Action == action)
            .OrderByDescending(a => a.ActionAt)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<Dictionary<Guid, Guid?>> GetLatestApproverUserIdsByWorkspaceAsync(
        Guid workspaceId,
        CancellationToken ct = default)
    {
        var approvalAudits = await _dbSet.AsNoTracking()
            .Where(a => a.WorkspaceId == workspaceId && 
                        a.Action == Domain.Constants.WorkspaceDocumentConstants.AuditActions.ApproveDocument &&
                        a.ActorId != null)
            .Select(a => new { a.DocumentId, a.ActorId, a.ActionAt })
            .ToListAsync(ct);

        return approvalAudits
            .GroupBy(a => a.DocumentId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(a => a.ActionAt).First().ActorId);
    }
}
