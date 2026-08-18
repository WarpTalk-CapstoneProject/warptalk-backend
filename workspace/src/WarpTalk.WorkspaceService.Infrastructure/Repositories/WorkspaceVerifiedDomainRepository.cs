using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Interfaces;
using WarpTalk.WorkspaceService.Infrastructure.Persistence;

namespace WarpTalk.WorkspaceService.Infrastructure.Repositories;

public class WorkspaceVerifiedDomainRepository : GenericRepository<WorkspaceVerifiedDomain>, IWorkspaceVerifiedDomainRepository
{
    public WorkspaceVerifiedDomainRepository(WorkspaceDbContext context) : base(context)
    {
    }

    public async Task<(List<WorkspaceVerifiedDomain> Items, int TotalCount)> GetPagedVerifiedDomainsAsync(
        Guid workspaceId,
        int page,
        int pageSize,
        bool isDescending = true,
        CancellationToken ct = default)
    {
        var query = _dbSet.AsNoTracking().Where(vd => vd.WorkspaceId == workspaceId && vd.RevokedAt == null);

        var totalCount = await query.CountAsync(ct);

        query = isDescending 
            ? query.OrderByDescending(vd => vd.CreatedAt) 
            : query.OrderBy(vd => vd.CreatedAt);

        var skip = Math.Max(0, (page - 1) * pageSize);
        var items = await query.Skip(skip).Take(pageSize).ToListAsync(ct);

        return (items, totalCount);
    }

    /// <summary>
    /// PostgreSQL reports a unique-index rejection as SQLSTATE 23505 and names the index that
    /// rejected it. Both are matched: the state alone would also catch a violation of some
    /// unrelated index, and answering "yes" to that would dress a genuine bug up as a polite
    /// business error.
    ///
    /// Matched on SqlState and ConstraintName rather than message text, which is localised and
    /// version-dependent. Walks the inner-exception chain because EF wraps the provider
    /// exception in a DbUpdateException.
    ///
    /// The index name is a constant here rather than a parameter: only one index enforces
    /// "a domain belongs to at most one workspace", this repository owns the table it sits on,
    /// and a caller that had to name it would be reaching into the schema from a layer that is
    /// supposed to know nothing about it.
    /// </summary>
    public bool IsDomainAlreadyClaimedViolation(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg
                && string.Equals(pg.ConstraintName, UniqueVerifiedDomainIndex, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The partial unique index from
    /// <c>008-03-06-2026-add-workspace-documents-and-glossary.sql</c>, redefined over
    /// <c>lower(domain)</c> by <c>20260813090000_verified_domain_uniqueness_case_insensitive.sql</c>.
    /// </summary>
    private const string UniqueVerifiedDomainIndex = "idx_workspace_verified_domains_unique_verified";
}
