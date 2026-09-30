using Microsoft.EntityFrameworkCore;
using WarpTalk.NotificationService.Domain.Constants;
using WarpTalk.NotificationService.Domain.Entities;
using WarpTalk.NotificationService.Domain.Interfaces;
using WarpTalk.NotificationService.Infrastructure.Persistence;

namespace WarpTalk.NotificationService.Infrastructure.Repositories;

public class EmailCampaignRecipientRepository : IEmailCampaignRecipientRepository
{
    private readonly NotificationDbContext _context;

    public EmailCampaignRecipientRepository(NotificationDbContext context)
    {
        _context = context;
    }

    public Task AddRangeAsync(IEnumerable<EmailCampaignRecipient> recipients, CancellationToken ct = default) =>
        _context.EmailCampaignRecipients.AddRangeAsync(recipients, ct);

    public async Task<IReadOnlyList<EmailCampaignRecipient>> NextPendingAsync(Guid campaignId, int batchSize, CancellationToken ct = default) =>
        await _context.EmailCampaignRecipients
            .Where(r => r.CampaignId == campaignId && r.Status == EmailCmsConstants.RecipientPending)
            .OrderBy(r => r.Id)
            .Take(batchSize)
            .ToListAsync(ct);

    public Task<bool> AnyAsync(Guid campaignId, CancellationToken ct = default) =>
        _context.EmailCampaignRecipients.AnyAsync(r => r.CampaignId == campaignId, ct);

    public async Task<(IReadOnlyList<EmailCampaignRecipient> Items, int Total)> PageAsync(
        Guid campaignId, string? status, int page, int pageSize, CancellationToken ct = default)
    {
        var query = _context.EmailCampaignRecipients
            .AsNoTracking()
            .Where(r => r.CampaignId == campaignId && (status == null || r.Status == status));
        var total = await query.CountAsync(ct);
        var items = await query
            .OrderBy(r => r.Email)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);
        return (items, total);
    }

    public Task<int> SkipPendingAsync(Guid campaignId, string reason, CancellationToken ct = default) =>
        _context.EmailCampaignRecipients
            .Where(r => r.CampaignId == campaignId && r.Status == EmailCmsConstants.RecipientPending)
            .ExecuteUpdateAsync(set => set
                .SetProperty(r => r.Status, EmailCmsConstants.RecipientSkipped)
                .SetProperty(r => r.Error, reason), ct);
}
