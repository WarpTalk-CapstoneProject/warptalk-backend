using Microsoft.EntityFrameworkCore;
using WarpTalk.NotificationService.Domain.Entities;
using WarpTalk.NotificationService.Domain.Interfaces;
using WarpTalk.NotificationService.Infrastructure.Persistence;

namespace WarpTalk.NotificationService.Infrastructure.Repositories;

public class EmailSampleDataSetRepository : IEmailSampleDataSetRepository
{
    private readonly NotificationDbContext _context;

    public EmailSampleDataSetRepository(NotificationDbContext context)
    {
        _context = context;
    }

    public async Task AddAsync(EmailSampleDataSet set, CancellationToken ct = default) =>
        await _context.EmailSampleDataSets.AddAsync(set, ct);

    public void Remove(EmailSampleDataSet set) => _context.EmailSampleDataSets.Remove(set);

    public Task<EmailSampleDataSet?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        _context.EmailSampleDataSets.FirstOrDefaultAsync(s => s.Id == id, ct);

    public async Task<IReadOnlyList<EmailSampleDataSet>> ListAsync(string templateKey, CancellationToken ct = default) =>
        await _context.EmailSampleDataSets
            .AsNoTracking()
            .Where(s => s.TemplateKey == templateKey)
            .OrderBy(s => s.CreatedAt)
            .ToListAsync(ct);
}
