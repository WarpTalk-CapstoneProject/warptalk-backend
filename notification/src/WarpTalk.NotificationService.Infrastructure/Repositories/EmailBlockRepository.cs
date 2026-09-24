using Microsoft.EntityFrameworkCore;
using WarpTalk.NotificationService.Domain.Constants;
using WarpTalk.NotificationService.Domain.Entities;
using WarpTalk.NotificationService.Domain.Interfaces;
using WarpTalk.NotificationService.Infrastructure.Persistence;

namespace WarpTalk.NotificationService.Infrastructure.Repositories;

public class EmailBlockRepository : IEmailBlockRepository
{
    private readonly NotificationDbContext _context;

    public EmailBlockRepository(NotificationDbContext context)
    {
        _context = context;
    }

    public async Task AddAsync(EmailBlock block, CancellationToken ct = default) =>
        await _context.EmailBlocks.AddAsync(block, ct);

    public void Remove(EmailBlock block) => _context.EmailBlocks.Remove(block);

    public Task<EmailBlock?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        _context.EmailBlocks.FirstOrDefaultAsync(b => b.Id == id, ct);

    public async Task<IReadOnlyList<EmailBlock>> ListAsync(string? kind, CancellationToken ct = default)
    {
        var query = _context.EmailBlocks.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(kind)) query = query.Where(b => b.Kind == kind);
        return await query.OrderBy(b => b.Kind).ThenBy(b => b.Name).ToListAsync(ct);
    }

    public Task<bool> KeyExistsAsync(string kind, string key, Guid? exceptId, CancellationToken ct = default) =>
        _context.EmailBlocks.AnyAsync(b => b.Kind == kind && b.Key == key && (exceptId == null || b.Id != exceptId), ct);

    public async Task<IReadOnlyList<EmailBlock>> GetDefaultLayoutsAsync(CancellationToken ct = default) =>
        await _context.EmailBlocks
            .Where(b => b.Kind == EmailCmsConstants.KindLayout && b.IsDefault)
            .ToListAsync(ct);

    public async Task<IReadOnlyDictionary<string, string>> GetPublishedPartialsAsync(CancellationToken ct = default)
    {
        var rows = await _context.EmailBlocks
            .AsNoTracking()
            .Where(b => b.Kind == EmailCmsConstants.KindPartial
                        && b.Status == EmailCmsConstants.StatusActive
                        && b.PublishedHtml != null)
            .Select(b => new { b.Key, b.PublishedHtml })
            .ToListAsync(ct);
        return rows.ToDictionary(row => row.Key, row => row.PublishedHtml!, StringComparer.Ordinal);
    }
}
