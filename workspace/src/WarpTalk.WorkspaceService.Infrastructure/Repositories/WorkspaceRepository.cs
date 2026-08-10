using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using WarpTalk.WorkspaceService.Domain.Constants;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Enums;
using WarpTalk.WorkspaceService.Domain.Interfaces;
using WarpTalk.WorkspaceService.Domain.ReadModels;
using WarpTalk.WorkspaceService.Domain.Settings;
using WarpTalk.Shared.Extensions;
using WarpTalk.WorkspaceService.Infrastructure.Persistence;


namespace WarpTalk.WorkspaceService.Infrastructure.Repositories;

public class WorkspaceRepository : GenericRepository<Workspace>, IWorkspaceRepository
{
    public WorkspaceRepository(WorkspaceDbContext context) : base(context)
    {
    }

    public async Task<(List<Workspace> Items, int TotalCount)> GetWorkspacesForUserAsync(Guid userId, int page, int pageSize, string? search = null, CancellationToken ct = default)
    {
        var activeMemberStatus = WorkspaceMemberStatus.Active.ToStorageValue();
        var query = _context.Workspaces
            .AsNoTracking()
            .Include(w => w.WorkspaceMembers.Where(m =>
                m.UserId == userId
                && m.RemovedAt == null
                && m.Status.ToLower() == activeMemberStatus))
            .Where(w =>
                w.DeletedAt == null
                && w.IsActive
                && w.WorkspaceMembers.Any(m =>
                    m.UserId == userId
                    && m.RemovedAt == null
                    && m.Status.ToLower() == activeMemberStatus));

        if (!string.IsNullOrWhiteSpace(search))
        {
            var searchLower = search.ToLower();
            query = query.Where(w => w.Name.ToLower().Contains(searchLower) || w.Slug.ToLower().Contains(searchLower));
        }

        return await query
            .OrderByDescending(w => w.CreatedAt)
            .ToPagedListAsync(page, pageSize, ct);
    }

    public async Task<(List<WorkspaceDirectoryRow> Items, int TotalCount)> GetAdminDirectoryAsync(
        WorkspaceDirectoryFilter filter,
        CancellationToken ct = default)
    {
        var query = _context.Workspaces.AsNoTracking();

        query = filter.Status switch
        {
            WorkspaceLifecycleStatus.Active => query.Where(w => w.DeletedAt == null && w.IsActive),
            WorkspaceLifecycleStatus.Suspended => query.Where(w => w.DeletedAt == null && !w.IsActive),
            WorkspaceLifecycleStatus.Deleted => query.Where(w => w.DeletedAt != null),
            _ => query,
        };

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var searchLower = filter.Search.Trim().ToLower();
            query = query.Where(w =>
                w.Name.ToLower().Contains(searchLower)
                || w.Slug.ToLower().Contains(searchLower));
        }

        if (filter.MinMembers.HasValue)
        {
            var minMembers = filter.MinMembers.Value;
            query = query.Where(w => w.WorkspaceMembers.Count(m => m.RemovedAt == null) >= minMembers);
        }

        if (filter.MaxMembers.HasValue)
        {
            var maxMembers = filter.MaxMembers.Value;
            query = query.Where(w => w.WorkspaceMembers.Count(m => m.RemovedAt == null) <= maxMembers);
        }

        IOrderedQueryable<Workspace> ordered = filter.Sort switch
        {
            WorkspaceDirectorySort.CreatedAsc => query.OrderBy(w => w.CreatedAt),
            WorkspaceDirectorySort.NameAsc => query.OrderBy(w => w.Name),
            WorkspaceDirectorySort.NameDesc => query.OrderByDescending(w => w.Name),
            WorkspaceDirectorySort.MembersAsc =>
                query.OrderBy(w => w.WorkspaceMembers.Count(m => m.RemovedAt == null)),
            WorkspaceDirectorySort.MembersDesc =>
                query.OrderByDescending(w => w.WorkspaceMembers.Count(m => m.RemovedAt == null)),
            WorkspaceDirectorySort.UpdatedDesc => query.OrderByDescending(w => w.UpdatedAt),
            _ => query.OrderByDescending(w => w.CreatedAt),
        };

        // Id is the tiebreaker so pages stay stable when the sort key repeats.
        ordered = ordered.ThenBy(w => w.Id);

        // Not ToPagedListAsync: the count must run against the filtered workspace rows, not
        // against the projection, so the per-row count subqueries only execute for one page.
        var safePage = filter.Page <= 0 ? 1 : filter.Page;
        var safePageSize = filter.PageSize <= 0 ? 20 : filter.PageSize;
        var totalCount = await ordered.CountAsync(ct);
        var items = await Project(ordered)
            .Skip((safePage - 1) * safePageSize)
            .Take(safePageSize)
            .ToListAsync(ct);

        return (items, totalCount);
    }

    public async Task<WorkspaceDirectoryRow?> GetAdminDetailAsync(Guid workspaceId, CancellationToken ct = default)
    {
        return await Project(_context.Workspaces.AsNoTracking().Where(w => w.Id == workspaceId))
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Counts are subqueries rather than Includes, so a page of the directory costs one row
    /// per workspace instead of every member, invitation, and document row behind it.
    /// </summary>
    private static IQueryable<WorkspaceDirectoryRow> Project(IQueryable<Workspace> query)
    {
        var internalType = MembershipType.Internal.ToString();
        var externalType = MembershipType.External.ToString();
        var pendingStatus = InvitationStatus.PENDING.ToString();
        var verifiedStatus = VerifiedDomainStatus.Verified.ToString().ToLowerInvariant();

        return query.Select(w => new WorkspaceDirectoryRow(
            w.Id,
            w.Name,
            w.Slug,
            w.LogoUrl,
            w.OwnerId,
            w.IsActive,
            w.DeletedAt,
            w.CreatedAt,
            w.UpdatedAt,
            w.AllowExternalCollaboration,
            w.RequireVerifiedDomainForInternal,
            w.WorkspaceMembers.Count(m => m.RemovedAt == null),
            w.WorkspaceMembers.Count(m => m.RemovedAt == null && m.MembershipType == internalType),
            w.WorkspaceMembers.Count(m => m.RemovedAt == null && m.MembershipType == externalType),
            w.WorkspaceInvitations.Count(i => i.Status == pendingStatus && i.AcceptedAt == null),
            w.WorkspaceDocuments.Count(d => d.DeletedAt == null),
            w.WorkspaceVerifiedDomains.Count(vd =>
                vd.Status == verifiedStatus && vd.VerifiedAt != null && vd.RevokedAt == null),
            w.WorkspaceMembers
                .Where(m => m.RemovedAt == null)
                .Max(m => (DateTime?)m.JoinedAt),
            w.WorkspaceDocuments
                .Where(d => d.DeletedAt == null)
                .Max(d => (DateTime?)d.CreatedAt)));
    }

    public async Task<WorkspaceConfiguration> GetSettingsAsync(Guid workspaceId, CancellationToken ct = default)
    {
        var workspace = await GetByIdAsync(workspaceId, ct);
        if (workspace == null)
        {
            return new WorkspaceConfiguration();
        }

        var settings = new WorkspaceConfiguration();
        if (!string.IsNullOrWhiteSpace(workspace.Settings) && workspace.Settings != "{}")
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<WorkspaceConfiguration>(workspace.Settings);
                if (parsed != null)
                {
                    settings = parsed;
                }
            }
            catch
            {
                // Fallback to default settings
            }
        }
        settings.AllowExternalCollaboration = workspace.AllowExternalCollaboration;
        settings.RequireVerifiedDomainForInternal = workspace.RequireVerifiedDomainForInternal;
        return settings;
    }

    public async Task<bool> UpdateSettingsAsync(Guid workspaceId, WorkspaceConfiguration settings, Guid userId, CancellationToken ct = default)
    {
        var workspace = await GetByIdAsync(workspaceId, ct);
        if (workspace == null)
        {
            return false;
        }

        workspace.Settings = JsonSerializer.Serialize(settings);
        workspace.AllowExternalCollaboration = settings.AllowExternalCollaboration;
        workspace.RequireVerifiedDomainForInternal = settings.RequireVerifiedDomainForInternal;
        workspace.UpdatedAt = DateTime.UtcNow;
        workspace.UpdatedBy = userId;

        Update(workspace);
        return true;
    }
}
