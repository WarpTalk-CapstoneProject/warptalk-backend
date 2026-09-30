using WarpTalk.NotificationService.Domain.Entities;

namespace WarpTalk.NotificationService.Domain.Interfaces;

public interface IEmailCampaignRecipientRepository
{
    Task AddRangeAsync(IEnumerable<EmailCampaignRecipient> recipients, CancellationToken ct = default);

    /// <summary>Tracked, oldest first: the next batch to send.</summary>
    Task<IReadOnlyList<EmailCampaignRecipient>> NextPendingAsync(Guid campaignId, int batchSize, CancellationToken ct = default);

    Task<bool> AnyAsync(Guid campaignId, CancellationToken ct = default);

    /// <summary>Untracked page, optionally one status only.</summary>
    Task<(IReadOnlyList<EmailCampaignRecipient> Items, int Total)> PageAsync(
        Guid campaignId, string? status, int page, int pageSize, CancellationToken ct = default);

    /// <summary>Marks every still-pending recipient of a cancelled send as skipped.</summary>
    Task<int> SkipPendingAsync(Guid campaignId, string reason, CancellationToken ct = default);
}
