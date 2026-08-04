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

public class AdminAuditLogRepository : IAdminAuditLogRepository
{
    private readonly WorkspaceDbContext _context;

    public AdminAuditLogRepository(WorkspaceDbContext context)
    {
        _context = context;
    }

    public async Task AppendAsync(WorkspaceAdminAction entry, CancellationToken ct = default)
    {
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
