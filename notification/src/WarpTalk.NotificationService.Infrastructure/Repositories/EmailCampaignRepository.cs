using Microsoft.EntityFrameworkCore;
using WarpTalk.NotificationService.Domain.Constants;
using WarpTalk.NotificationService.Domain.Entities;
using WarpTalk.NotificationService.Domain.Interfaces;
using WarpTalk.NotificationService.Infrastructure.Persistence;

namespace WarpTalk.NotificationService.Infrastructure.Repositories;

public class EmailCampaignRepository : IEmailCampaignRepository
{
    private readonly NotificationDbContext _context;

    public EmailCampaignRepository(NotificationDbContext context)
    {
        _context = context;
    }

    public async Task AddAsync(EmailCampaign campaign, CancellationToken ct = default) =>
        await _context.EmailCampaigns.AddAsync(campaign, ct);

    public Task<EmailCampaign?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        _context.EmailCampaigns.FirstOrDefaultAsync(c => c.Id == id, ct);

    public async Task<IReadOnlyList<EmailCampaign>> ListForTemplateAsync(string templateKey, int limit, CancellationToken ct = default) =>
        await _context.EmailCampaigns
            .AsNoTracking()
            .Where(c => c.TemplateKey == templateKey)
            .OrderByDescending(c => c.CreatedAt)
            .Take(limit)
            .ToListAsync(ct);

    public Task<int> CountCreatedSinceAsync(Guid createdBy, DateTime since, CancellationToken ct = default) =>
        _context.EmailCampaigns.CountAsync(c => c.CreatedBy == createdBy && c.Source == EmailCmsConstants.CampaignSourceManual && c.CreatedAt >= since, ct);

    public Task<bool> AnySentAsync(string templateKey, CancellationToken ct = default) =>
        _context.EmailCampaigns.AnyAsync(c => c.TemplateKey == templateKey && c.SentCount > 0, ct);

    public async Task<EmailCampaign?> NextDueAsync(DateTime now, CancellationToken ct = default) =>
        await _context.EmailCampaigns
            .Where(c => c.Status == EmailCmsConstants.CampaignSending)
            .OrderBy(c => c.StartedAt)
            .FirstOrDefaultAsync(ct)
        ?? await _context.EmailCampaigns
            .Where(c => c.Status == EmailCmsConstants.CampaignQueued && c.ScheduledAt <= now)
            .OrderBy(c => c.ScheduledAt)
            .FirstOrDefaultAsync(ct);
}
