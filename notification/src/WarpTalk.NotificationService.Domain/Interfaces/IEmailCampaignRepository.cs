using WarpTalk.NotificationService.Domain.Entities;

namespace WarpTalk.NotificationService.Domain.Interfaces;

public interface IEmailCampaignRepository
{
    Task AddAsync(EmailCampaign campaign, CancellationToken ct = default);

    /// <summary>Tracked, for updating.</summary>
    Task<EmailCampaign?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Untracked, newest first.</summary>
    Task<IReadOnlyList<EmailCampaign>> ListForTemplateAsync(string templateKey, int limit, CancellationToken ct = default);

    /// <summary>How many sends this admin started since <paramref name="since"/> — the rate limit.</summary>
    Task<int> CountCreatedSinceAsync(Guid createdBy, DateTime since, CancellationToken ct = default);

    /// <summary>Whether this template ever handed an email to the provider through a send.</summary>
    Task<bool> AnySentAsync(string templateKey, CancellationToken ct = default);

    /// <summary>
    /// The next send to work on, tracked: one already SENDING (a restart resumes it) before the
    /// oldest due QUEUED one.
    /// </summary>
    Task<EmailCampaign?> NextDueAsync(DateTime now, CancellationToken ct = default);
}
