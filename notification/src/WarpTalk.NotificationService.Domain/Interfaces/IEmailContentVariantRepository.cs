using WarpTalk.NotificationService.Domain.Entities;

namespace WarpTalk.NotificationService.Domain.Interfaces;

public interface IEmailContentVariantRepository
{
    Task AddAsync(EmailContentVariant variant, CancellationToken ct = default);

    void Remove(EmailContentVariant variant);

    /// <summary>Tracked, for updating.</summary>
    Task<EmailContentVariant?> GetAsync(string templateKey, string locale, CancellationToken ct = default);

    /// <summary>Untracked. Every variant, or every variant of one email.</summary>
    Task<IReadOnlyList<EmailContentVariant>> ListAsync(string? templateKey, CancellationToken ct = default);

    /// <summary>Untracked. The active, published variant for a key and locale — what a send reads.</summary>
    Task<EmailContentVariant?> GetPublishedAsync(string templateKey, string locale, CancellationToken ct = default);

    /// <summary>Untracked. Active variants whose draft or published content uses a layout.</summary>
    Task<IReadOnlyList<EmailContentVariant>> ListUsingLayoutAsync(Guid layoutId, CancellationToken ct = default);
}
