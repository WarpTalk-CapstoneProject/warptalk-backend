namespace WarpTalk.NotificationService.Domain.Interfaces;

public interface IUnitOfWork : IDisposable
{
    INotificationMessageRepository NotificationMessageRepository { get; }
    INotificationPreferenceRepository NotificationPreferenceRepository { get; }
    INotificationTemplateRepository NotificationTemplateRepository { get; }
    IPushSubscriptionRepository PushSubscriptionRepository { get; }
    IAdminNotificationRepository AdminNotificationRepository { get; }
    INotificationInboxMessageRepository NotificationInboxMessageRepository { get; }
    INotificationTemplateVersionRepository NotificationTemplateVersionRepository { get; }
    IAnnouncementRepository AnnouncementRepository { get; }
    IAnnouncementDismissalRepository AnnouncementDismissalRepository { get; }
    Task<int> SaveChangesAsync();
    Task BeginTransactionAsync(CancellationToken ct = default);
    Task CommitTransactionAsync(CancellationToken ct = default);
    Task RollbackTransactionAsync(CancellationToken ct = default);
}
