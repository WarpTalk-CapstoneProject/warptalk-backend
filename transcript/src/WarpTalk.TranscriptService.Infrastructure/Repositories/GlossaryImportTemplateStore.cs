using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using WarpTalk.TranscriptService.Application.GlossaryImportTemplates;
using WarpTalk.TranscriptService.Domain.Entities;
using WarpTalk.TranscriptService.Infrastructure.Persistence.Contexts;

namespace WarpTalk.TranscriptService.Infrastructure.Repositories;

/// <inheritdoc cref="IGlossaryImportTemplateStore"/>
public sealed class GlossaryImportTemplateStore : IGlossaryImportTemplateStore
{
    private readonly TranscriptDbContext _context;

    public GlossaryImportTemplateStore(TranscriptDbContext context)
    {
        _context = context;
    }

    public Task<GlossaryImportTemplate?> GetAsync(CancellationToken ct = default)
        => _context.GlossaryImportTemplates
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == GlossaryImportTemplate.SingletonId, ct);

    public async Task<GlossaryImportTemplate> SaveAsync(string configJson, Guid actorId, DateTime nowUtc, CancellationToken ct = default)
    {
        // Tracked, so the [AdminAudited] interceptor sees a before/after diff on an update.
        var row = await _context.GlossaryImportTemplates
            .FirstOrDefaultAsync(t => t.Id == GlossaryImportTemplate.SingletonId, ct);
        if (row is null)
        {
            row = new GlossaryImportTemplate { Id = GlossaryImportTemplate.SingletonId };
            _context.GlossaryImportTemplates.Add(row);
        }

        row.Config = configJson;
        row.UpdatedAt = nowUtc;
        row.UpdatedBy = actorId;
        await _context.SaveChangesAsync(ct);
        return row;
    }

    public async Task DeleteAsync(CancellationToken ct = default)
    {
        var row = await _context.GlossaryImportTemplates
            .FirstOrDefaultAsync(t => t.Id == GlossaryImportTemplate.SingletonId, ct);
        if (row is null) return;

        _context.GlossaryImportTemplates.Remove(row);
        await _context.SaveChangesAsync(ct);
    }
}
