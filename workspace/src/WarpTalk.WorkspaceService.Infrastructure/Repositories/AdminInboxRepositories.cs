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

public sealed class AdminInboxStateRepository : IAdminInboxStateRepository
{
    private readonly WorkspaceDbContext _context;

    public AdminInboxStateRepository(WorkspaceDbContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyList<AdminInboxItemState>> GetManyAsync(IReadOnlyCollection<string> keys, CancellationToken ct = default)
    {
        if (keys.Count == 0) return [];
        return await _context.AdminInboxItemStates.AsNoTracking().Where(s => keys.Contains(s.ItemKey)).ToListAsync(ct);
    }

    public Task<AdminInboxItemState?> GetAsync(string key, CancellationToken ct = default)
        => _context.AdminInboxItemStates.FirstOrDefaultAsync(s => s.ItemKey == key, ct);

    public async Task AddAsync(AdminInboxItemState state, CancellationToken ct = default)
        => await _context.AdminInboxItemStates.AddAsync(state, ct);
}

public sealed class AdminInboxNoteRepository : IAdminInboxNoteRepository
{
    private readonly WorkspaceDbContext _context;

    public AdminInboxNoteRepository(WorkspaceDbContext context)
    {
        _context = context;
    }

    public async Task AppendAsync(AdminInboxNote note, CancellationToken ct = default)
        => await _context.AdminInboxNotes.AddAsync(note, ct);

    public async Task<IReadOnlyList<AdminInboxNote>> GetForItemAsync(string key, int limit, CancellationToken ct = default)
        => await _context.AdminInboxNotes.AsNoTracking()
            .Where(n => n.ItemKey == key)
            .OrderBy(n => n.CreatedAt)
            .ThenBy(n => n.Id)
            .Take(limit)
            .ToListAsync(ct);

    public async Task<IReadOnlyDictionary<string, (int Count, DateTime LastAt)>> SummarizeAsync(
        IReadOnlyCollection<string> keys, CancellationToken ct = default)
    {
        if (keys.Count == 0) return new Dictionary<string, (int, DateTime)>();
        var rows = await _context.AdminInboxNotes.AsNoTracking()
            .Where(n => keys.Contains(n.ItemKey))
            .GroupBy(n => n.ItemKey)
            .Select(g => new { Key = g.Key, Count = g.Count(), LastAt = g.Max(n => n.CreatedAt) })
            .ToListAsync(ct);
        return rows.ToDictionary(r => r.Key, r => (r.Count, r.LastAt), StringComparer.Ordinal);
    }
}
