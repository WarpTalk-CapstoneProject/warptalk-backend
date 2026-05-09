using WarpTalk.NotificationService.Domain.Entities;

namespace WarpTalk.NotificationService.Domain.Interfaces;

public interface INotificationMessageRepository : IGenericRepository<NotificationMessage>
{
    Task<(IEnumerable<NotificationMessage> Items, int TotalCount)> GetPaginatedByUserIdAsync(Guid userId, int page, int pageSize, CancellationToken ct = default);
    Task<NotificationMessage?> GetByIdAndUserIdAsync(Guid id, Guid userId, CancellationToken ct = default);
    Task<IEnumerable<NotificationMessage>> GetUnreadByUserIdAsync(Guid userId, CancellationToken ct = default);
    Task<bool> MarkAsReadAsync(Guid id, Guid userId, CancellationToken ct = default);
    Task<int> MarkAllAsReadAsync(Guid userId, CancellationToken ct = default);
}
