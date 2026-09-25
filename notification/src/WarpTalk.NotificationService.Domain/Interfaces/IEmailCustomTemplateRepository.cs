using WarpTalk.NotificationService.Domain.Entities;

namespace WarpTalk.NotificationService.Domain.Interfaces;

public interface IEmailCustomTemplateRepository
{
    Task AddAsync(EmailCustomTemplate template, CancellationToken ct = default);

    void Remove(EmailCustomTemplate template);

    /// <summary>Tracked, for updating. Includes deleted ones.</summary>
    Task<EmailCustomTemplate?> GetByKeyAsync(string key, CancellationToken ct = default);

    /// <summary>Untracked, by name. Deleted ones only when asked for.</summary>
    Task<IReadOnlyList<EmailCustomTemplate>> ListAsync(bool includeDeleted, CancellationToken ct = default);
}
