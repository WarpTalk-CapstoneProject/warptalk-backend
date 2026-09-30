using Microsoft.EntityFrameworkCore;
using WarpTalk.NotificationService.Domain.Constants;
using WarpTalk.NotificationService.Domain.Entities;
using WarpTalk.NotificationService.Domain.Interfaces;
using WarpTalk.NotificationService.Infrastructure.Persistence;

namespace WarpTalk.NotificationService.Infrastructure.Repositories;

public class EmailCustomTemplateRepository : IEmailCustomTemplateRepository
{
    private readonly NotificationDbContext _context;

    public EmailCustomTemplateRepository(NotificationDbContext context)
    {
        _context = context;
    }

    public async Task AddAsync(EmailCustomTemplate template, CancellationToken ct = default) =>
        await _context.EmailCustomTemplates.AddAsync(template, ct);

    public void Remove(EmailCustomTemplate template) => _context.EmailCustomTemplates.Remove(template);

    public Task<EmailCustomTemplate?> GetByKeyAsync(string key, CancellationToken ct = default) =>
        _context.EmailCustomTemplates.FirstOrDefaultAsync(t => t.Key == key, ct);

    public async Task<IReadOnlyList<EmailCustomTemplate>> ListAsync(bool includeDeleted, CancellationToken ct = default) =>
        await _context.EmailCustomTemplates
            .AsNoTracking()
            .Where(t => includeDeleted || t.Status == EmailCmsConstants.StatusActive)
            .OrderBy(t => t.Name)
            .ToListAsync(ct);
}
