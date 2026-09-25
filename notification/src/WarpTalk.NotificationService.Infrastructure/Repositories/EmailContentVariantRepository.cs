using Microsoft.EntityFrameworkCore;
using WarpTalk.NotificationService.Domain.Constants;
using WarpTalk.NotificationService.Domain.Entities;
using WarpTalk.NotificationService.Domain.Interfaces;
using WarpTalk.NotificationService.Infrastructure.Persistence;

namespace WarpTalk.NotificationService.Infrastructure.Repositories;

public class EmailContentVariantRepository : IEmailContentVariantRepository
{
    private readonly NotificationDbContext _context;

    public EmailContentVariantRepository(NotificationDbContext context)
    {
        _context = context;
    }

    public async Task AddAsync(EmailContentVariant variant, CancellationToken ct = default) =>
        await _context.EmailContentVariants.AddAsync(variant, ct);

    public void Remove(EmailContentVariant variant) => _context.EmailContentVariants.Remove(variant);

    public Task<EmailContentVariant?> GetAsync(string templateKey, string locale, CancellationToken ct = default) =>
        _context.EmailContentVariants.FirstOrDefaultAsync(v => v.TemplateKey == templateKey && v.Locale == locale, ct);

    public async Task<IReadOnlyList<EmailContentVariant>> ListAsync(string? templateKey, CancellationToken ct = default)
    {
        var query = _context.EmailContentVariants.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(templateKey)) query = query.Where(v => v.TemplateKey == templateKey);
        return await query.OrderBy(v => v.TemplateKey).ThenBy(v => v.Locale).ToListAsync(ct);
    }

    public Task<EmailContentVariant?> GetPublishedAsync(string templateKey, string locale, CancellationToken ct = default) =>
        _context.EmailContentVariants
            .AsNoTracking()
            .FirstOrDefaultAsync(v => v.TemplateKey == templateKey
                                      && v.Locale == locale
                                      && v.Status == EmailCmsConstants.StatusActive
                                      && v.PublishedVersion > 0
                                      && v.PublishedBodyHtml != null, ct);

    public async Task<IReadOnlyList<EmailContentVariant>> ListUsingLayoutAsync(Guid layoutId, CancellationToken ct = default) =>
        await _context.EmailContentVariants
            .AsNoTracking()
            .Where(v => v.Status == EmailCmsConstants.StatusActive
                        && (v.DraftLayoutId == layoutId || v.PublishedLayoutId == layoutId))
            .ToListAsync(ct);
}
