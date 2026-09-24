using WarpTalk.NotificationService.Domain.Entities;

namespace WarpTalk.NotificationService.Domain.Interfaces;

public interface INotificationTemplateRepository : IGenericRepository<NotificationTemplate>
{
    /// <summary>The row for a template key on a channel, active or not. Tracked, for updating.</summary>
    Task<NotificationTemplate?> GetByTypeAsync(string type, string channel, CancellationToken ct = default);

    /// <summary>
    /// The active row for a template key on a channel, or null. What every sender reads, so it is
    /// untracked.
    /// </summary>
    Task<NotificationTemplate?> GetActiveByTypeAsync(string type, string channel, CancellationToken ct = default);

    /// <summary>Every row on a channel, active or not.</summary>
    Task<IReadOnlyList<NotificationTemplate>> ListByChannelAsync(string channel, CancellationToken ct = default);
}
