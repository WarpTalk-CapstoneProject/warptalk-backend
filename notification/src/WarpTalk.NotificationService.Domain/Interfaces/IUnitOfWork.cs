namespace WarpTalk.NotificationService.Domain.Interfaces;

public interface IUnitOfWork : IDisposable
{
    INotificationMessageRepository NotificationMessageRepository { get; }
    INotificationPreferenceRepository NotificationPreferenceRepository { get; }
    INotificationTemplateRepository NotificationTemplateRepository { get; }
    IPushSubscriptionRepository PushSubscriptionRepository { get; }
    IAdminNotificationRepository AdminNotificationRepository { get; }
    IGenericRepository<T> Repository<T>() where T : class;
    Task<int> SaveChangesAsync();
}
