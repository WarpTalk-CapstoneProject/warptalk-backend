using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using WarpTalk.Shared.AdminAudit;
using WarpTalk.Shared.Events;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Interfaces;
using WarpTalk.WorkspaceService.Infrastructure.Persistence;

namespace WarpTalk.WorkspaceService.Infrastructure.Repositories;

public class AdminAuditLogRepository : IAdminAuditLogRepository
{
    /// <summary>Rows sharing one instant are read whole; more than this in one microsecond is not a real log.</summary>
    private const int MaxTiesPerInstant = 1000;

    private readonly WorkspaceDbContext _context;
    private readonly IHttpContextAccessor? _httpContextAccessor;

    public AdminAuditLogRepository(WorkspaceDbContext context, IHttpContextAccessor? httpContextAccessor = null)
    {
        _context = context;
        _httpContextAccessor = httpContextAccessor;
    }

    /// <summary>
    /// Appends, and fills in the admin's e-mail, address and user agent from the HTTP request that
    /// is writing — this service's own admin endpoints. Fields a producer already set are kept, and
    /// a gRPC call (another service recording through <c>AdminAuditGrpcService</c>) stamps nothing:
    /// its request belongs to that service, and the caller sent the admin's own values instead.
    /// </summary>
    public async Task AppendAsync(WorkspaceAdminAction entry, CancellationToken ct = default)
    {
        var metadata = AdminAuditRequestMetadata.FromHttpContext(_httpContextAccessor?.HttpContext);
        entry.ActorEmail ??= metadata.ActorEmail;
        entry.ActorName ??= metadata.ActorName;
        entry.IpAddress ??= metadata.IpAddress;
        entry.UserAgent ??= metadata.UserAgent;

        await _context.WorkspaceAdminActions.AddAsync(entry, ct);
    }

    public Task<bool> ExistsAsync(
        string sourceService,
        string? correlationId,
        string action,
        Guid? entityId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(correlationId))
        {
            // Without a correlation id there is nothing to deduplicate on; the caller must
            // accept the append rather than guess that two similar rows are the same event.
            return Task.FromResult(false);
        }

        return _context.WorkspaceAdminActions
            .AsNoTracking()
            .AnyAsync(
                row => row.SourceService == sourceService
                       && row.CorrelationId == correlationId
                       && row.Action == action
                       && row.EntityId == entityId,
                ct);
    }

    public async Task<(List<WorkspaceAdminAction> Items, int TotalCount)> QueryAsync(
        AdminAuditLogFilter filter,
        CancellationToken ct = default)
    {
        var query = _context.WorkspaceAdminActions.AsNoTracking();

        if (filter.ActorId is { } actorId)
            query = query.Where(row => row.PerformedBy == actorId);

        if (!string.IsNullOrWhiteSpace(filter.Action))
            query = query.Where(row => row.Action == filter.Action);

        if (!string.IsNullOrWhiteSpace(filter.EntityType))
            query = query.Where(row => row.EntityType == filter.EntityType);

        if (filter.EntityTypes is { } entityTypes)
        {
            var allowed = entityTypes.ToList();
            query = query.Where(row => allowed.Contains(row.EntityType));
        }

        if (filter.EntityId is { } entityId)
            query = query.Where(row => row.EntityId == entityId);

        if (filter.WorkspaceId is { } workspaceId)
            query = query.Where(row => row.WorkspaceId == workspaceId);

        if (!string.IsNullOrWhiteSpace(filter.SourceService))
            query = query.Where(row => row.SourceService == filter.SourceService);

        if (!string.IsNullOrWhiteSpace(filter.Result))
            query = query.Where(row => row.Result == filter.Result);

        if (filter.From is { } from)
            query = query.Where(row => row.PerformedAt >= from);

        if (filter.To is { } to)
            query = query.Where(row => row.PerformedAt < to);

        // Id breaks ties so two actions recorded in the same instant keep a stable order
        // across pages — required for deterministic ordering.
        var ordered = query
            .OrderByDescending(row => row.PerformedAt)
            .ThenByDescending(row => row.Id);

        var safePage = filter.Page <= 0 ? 1 : filter.Page;
        var safePageSize = filter.PageSize <= 0 ? 20 : filter.PageSize;

        var totalCount = await ordered.CountAsync(ct);
        var items = await ordered
            .Skip((safePage - 1) * safePageSize)
            .Take(safePageSize)
            .ToListAsync(ct);

        return (items, totalCount);
    }

    public async Task<List<WorkspaceAdminAction>> SearchAsync(AdminAuditLogSearch search, CancellationToken ct = default)
    {
        var filtered = ApplyFilters(_context.WorkspaceAdminActions.AsNoTracking(), search);
        var take = Math.Max(1, search.Take);

        if (search.AfterPerformedAt is not { } afterAt || search.AfterId is not { } afterId)
        {
            return await filtered
                .OrderByDescending(row => row.PerformedAt)
                .ThenByDescending(row => row.Id)
                .Take(take)
                .ToListAsync(ct);
        }

        // Keyset without comparing GUIDs in LINQ: the rows sharing the cursor's instant are read in
        // the database's own id order and the cursor is found among them; everything older is a
        // plain timestamp comparison. Postgres and .NET order GUIDs differently, so the position
        // must come from the database's ordering, not from Guid.CompareTo.
        var ties = await filtered
            .Where(row => row.PerformedAt == afterAt)
            .OrderByDescending(row => row.Id)
            .Take(MaxTiesPerInstant)
            .ToListAsync(ct);
        var cursorIndex = ties.FindIndex(row => row.Id == afterId);
        var page = cursorIndex >= 0 ? ties.Skip(cursorIndex + 1).Take(take).ToList() : new List<WorkspaceAdminAction>();

        if (page.Count < take)
        {
            page.AddRange(await filtered
                .Where(row => row.PerformedAt < afterAt)
                .OrderByDescending(row => row.PerformedAt)
                .ThenByDescending(row => row.Id)
                .Take(take - page.Count)
                .ToListAsync(ct));
        }

        return page;
    }

    private IQueryable<WorkspaceAdminAction> ApplyFilters(IQueryable<WorkspaceAdminAction> query, AdminAuditLogSearch search)
    {
        if (search.ActorId is { } actorId)
            query = query.Where(row => row.PerformedBy == actorId);
        if (!string.IsNullOrWhiteSpace(search.Action))
            query = query.Where(row => row.Action == search.Action);
        if (!string.IsNullOrWhiteSpace(search.EntityType))
            query = query.Where(row => row.EntityType == search.EntityType);
        if (search.EntityId is { } entityId)
            query = query.Where(row => row.EntityId == entityId);
        if (!string.IsNullOrWhiteSpace(search.EntityKey))
            query = query.Where(row => row.EntityKey == search.EntityKey);
        if (search.WorkspaceId is { } workspaceId)
            query = query.Where(row => row.WorkspaceId == workspaceId);
        if (!string.IsNullOrWhiteSpace(search.SourceService))
            query = query.Where(row => row.SourceService == search.SourceService);
        if (!string.IsNullOrWhiteSpace(search.Result))
            query = query.Where(row => row.Result == search.Result);
        if (search.From is { } from)
            query = query.Where(row => row.PerformedAt >= from);
        if (search.To is { } to)
            query = query.Where(row => row.PerformedAt < to);

        if (!string.IsNullOrWhiteSpace(search.Text))
        {
            var text = search.Text.Trim();
            if (Guid.TryParse(text, out var id))
            {
                query = query.Where(row => row.Id == id || row.EntityId == id || row.WorkspaceId == id || row.PerformedBy == id);
            }
            else
            {
                var pattern = "%" + EscapeLike(text) + "%";
                var workspaceIds = _context.Workspaces
                    .Where(workspace => EF.Functions.ILike(workspace.Name, pattern, LikeEscape))
                    .Select(workspace => (Guid?)workspace.Id);
                query = query.Where(row =>
                    EF.Functions.ILike(row.Reason, pattern, LikeEscape)
                    || EF.Functions.ILike(row.Action, pattern, LikeEscape)
                    || EF.Functions.ILike(row.EntityType, pattern, LikeEscape)
                    || EF.Functions.ILike(row.SourceService, pattern, LikeEscape)
                    || (row.EntityLabel != null && EF.Functions.ILike(row.EntityLabel, pattern, LikeEscape))
                    || (row.EntityKey != null && EF.Functions.ILike(row.EntityKey, pattern, LikeEscape))
                    || (row.ActorEmail != null && EF.Functions.ILike(row.ActorEmail, pattern, LikeEscape))
                    || (row.ActorName != null && EF.Functions.ILike(row.ActorName, pattern, LikeEscape))
                    || (row.ErrorMessage != null && EF.Functions.ILike(row.ErrorMessage, pattern, LikeEscape))
                    || (row.CorrelationId != null && EF.Functions.ILike(row.CorrelationId, pattern, LikeEscape))
                    || (row.IpAddress != null && EF.Functions.ILike(row.IpAddress, pattern, LikeEscape))
                    || workspaceIds.Contains(row.WorkspaceId));
            }
        }

        // An attempt recorded before its commit, whose commit then failed, is told by its own
        // failed entry (same source, action and subject, correlation + ":failed"). Showing both
        // would say the change happened AND did not.
        var all = _context.WorkspaceAdminActions;
        query = query.Where(row => row.Result != AdminAuditResults.Succeeded
            || row.CorrelationId == null
            || !all.Any(failed =>
                failed.Result == AdminAuditResults.Failed
                && failed.SourceService == row.SourceService
                && failed.Action == row.Action
                && failed.EntityId == row.EntityId
                && failed.CorrelationId == row.CorrelationId + AdminAuditActionFilter.FailedSuffix));

        return query;
    }

    private const string LikeEscape = "\\";

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    public Task<WorkspaceAdminAction?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        _context.WorkspaceAdminActions.AsNoTracking().FirstOrDefaultAsync(row => row.Id == id, ct);

    public async Task<AdminAuditLogFacetRows> GetFacetsAsync(CancellationToken ct = default)
    {
        var rows = _context.WorkspaceAdminActions.AsNoTracking();

        var actions = await rows.GroupBy(row => row.Action)
            .Select(group => new { group.Key, Count = group.Count() })
            .OrderByDescending(group => group.Count)
            .ToListAsync(ct);
        var entityTypes = await rows.GroupBy(row => row.EntityType)
            .Select(group => new { group.Key, Count = group.Count() })
            .OrderByDescending(group => group.Count)
            .ToListAsync(ct);
        var sources = await rows.GroupBy(row => row.SourceService)
            .Select(group => new { group.Key, Count = group.Count() })
            .OrderByDescending(group => group.Count)
            .ToListAsync(ct);
        var actors = await rows.GroupBy(row => row.PerformedBy)
            .Select(group => new
            {
                group.Key,
                Count = group.Count(),
                Email = group.Where(row => row.ActorEmail != null)
                    .OrderByDescending(row => row.PerformedAt)
                    .Select(row => row.ActorEmail)
                    .FirstOrDefault(),
                Name = group.Where(row => row.ActorName != null)
                    .OrderByDescending(row => row.PerformedAt)
                    .Select(row => row.ActorName)
                    .FirstOrDefault(),
            })
            .OrderByDescending(group => group.Count)
            .Take(200)
            .ToListAsync(ct);

        return new AdminAuditLogFacetRows(
            actions.Select(item => new AdminAuditFacetCount(item.Key, item.Count)).ToList(),
            entityTypes.Select(item => new AdminAuditFacetCount(item.Key, item.Count)).ToList(),
            sources.Select(item => new AdminAuditFacetCount(item.Key, item.Count)).ToList(),
            actors.Select(item => new AdminAuditActorCount(item.Key, item.Count, item.Email, item.Name)).ToList());
    }

    public async Task<Dictionary<Guid, AdminAuditWorkspaceName>> GetWorkspaceNamesAsync(
        IReadOnlyCollection<Guid> workspaceIds,
        CancellationToken ct = default)
    {
        if (workspaceIds.Count == 0) return new Dictionary<Guid, AdminAuditWorkspaceName>();

        var ids = workspaceIds.Distinct().ToList();
        // IgnoreQueryFilters: a deleted workspace keeps its name in the log it left behind.
        var rows = await _context.Workspaces
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(workspace => ids.Contains(workspace.Id))
            .Select(workspace => new { workspace.Id, workspace.Name, workspace.Slug })
            .ToListAsync(ct);

        return rows.ToDictionary(row => row.Id, row => new AdminAuditWorkspaceName(row.Id, row.Name, row.Slug));
    }

    public Task<List<WorkspaceAdminAction>> GetForEntityAsync(
        string entityType,
        Guid entityId,
        int limit,
        CancellationToken ct = default) =>
        _context.WorkspaceAdminActions
            .AsNoTracking()
            .Where(row => row.EntityType == entityType && row.EntityId == entityId)
            .OrderByDescending(row => row.PerformedAt)
            .ThenByDescending(row => row.Id)
            .Take(limit)
            .ToListAsync(ct);
}
