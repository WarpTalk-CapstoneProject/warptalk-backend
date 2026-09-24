using WarpTalk.NotificationService.Domain.Entities;

namespace WarpTalk.NotificationService.Domain.Interfaces;

/// <summary>An email template's history. Append-only: nothing here updates or deletes.</summary>
public interface INotificationTemplateVersionRepository
{
    Task AddAsync(NotificationTemplateVersion version, CancellationToken ct = default);

    /// <summary>Newest first.</summary>
    Task<IReadOnlyList<NotificationTemplateVersion>> ListAsync(string type, string channel, int take, CancellationToken ct = default);

    Task<NotificationTemplateVersion?> GetAsync(string type, string channel, int version, CancellationToken ct = default);
}
