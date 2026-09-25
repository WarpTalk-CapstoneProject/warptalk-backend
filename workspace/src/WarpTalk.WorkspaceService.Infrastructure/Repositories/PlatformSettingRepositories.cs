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

public sealed class PlatformSettingValueRepository : IPlatformSettingValueRepository
{
    private readonly WorkspaceDbContext _context;

    public PlatformSettingValueRepository(WorkspaceDbContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyList<PlatformSettingValue>> GetAllAsync(CancellationToken ct = default)
        => await _context.PlatformSettingValues.AsNoTracking().ToListAsync(ct);

    public Task<PlatformSettingValue?> GetAsync(string key, string scopeType, string scopeId, CancellationToken ct = default)
        => _context.PlatformSettingValues.FirstOrDefaultAsync(
            v => v.SettingKey == key && v.ScopeType == scopeType && v.ScopeId == scopeId, ct);

    public async Task AddAsync(PlatformSettingValue value, CancellationToken ct = default)
        => await _context.PlatformSettingValues.AddAsync(value, ct);

    public void Remove(PlatformSettingValue value) => _context.PlatformSettingValues.Remove(value);
}

public sealed class PlatformSettingChangeRepository : IPlatformSettingChangeRepository
{
    private readonly WorkspaceDbContext _context;

    public PlatformSettingChangeRepository(WorkspaceDbContext context)
    {
        _context = context;
    }

    public async Task AppendAsync(PlatformSettingChange change, CancellationToken ct = default)
        => await _context.PlatformSettingChanges.AddAsync(change, ct);

    public Task<PlatformSettingChange?> GetAsync(Guid id, CancellationToken ct = default)
        => _context.PlatformSettingChanges.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);

    public async Task<IReadOnlyList<PlatformSettingChange>> GetRecentAsync(string? key, int limit, CancellationToken ct = default)
    {
        var query = _context.PlatformSettingChanges.AsNoTracking();
        if (key is not null) query = query.Where(c => c.SettingKey == key);
        return await query.OrderByDescending(c => c.ChangedAt).ThenByDescending(c => c.Id).Take(limit).ToListAsync(ct);
    }

    public async Task<IReadOnlyDictionary<string, PlatformSettingChange>> GetLatestPerKeyAsync(CancellationToken ct = default)
    {
        // DISTINCT ON is one index scan over (setting_key, changed_at DESC) and needs no GroupBy
        // translation (a LINQ shape that only fails at runtime, against a real database).
        var latest = await _context.PlatformSettingChanges
            .FromSqlRaw(
                "SELECT DISTINCT ON (setting_key) * FROM workspace.platform_setting_changes ORDER BY setting_key, changed_at DESC, id DESC")
            .AsNoTracking()
            .ToListAsync(ct);
        return latest.ToDictionary(c => c.SettingKey, StringComparer.Ordinal);
    }
}
