using WarpTalk.NotificationService.Domain.Entities;

namespace WarpTalk.NotificationService.Domain.Interfaces;

/// <summary>Published versions of email content and blocks. Append-only.</summary>
public interface IEmailCmsVersionRepository
{
    Task AddAsync(EmailCmsVersion version, CancellationToken ct = default);

    /// <summary>Newest first.</summary>
    Task<IReadOnlyList<EmailCmsVersion>> ListAsync(string ownerType, Guid ownerId, int take, CancellationToken ct = default);

    Task<EmailCmsVersion?> GetAsync(string ownerType, Guid ownerId, int version, CancellationToken ct = default);
}
