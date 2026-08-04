using WarpTalk.NotificationService.Domain.Interfaces;
using WarpTalk.NotificationService.Infrastructure.Persistence;

namespace WarpTalk.NotificationService.Infrastructure.Repositories;

public class UnitOfWork : IUnitOfWork
{
    private readonly NotificationDbContext _context;
    private INotificationMessageRepository? _notificationMessageRepository;
    private INotificationPreferenceRepository? _notificationPreferenceRepository;
    private INotificationTemplateRepository? _notificationTemplateRepository;
    private IPushSubscriptionRepository? _pushSubscriptionRepository;
    private IAdminNotificationRepository? _adminNotificationRepository;
    private INotificationInboxMessageRepository? _notificationInboxMessageRepository;

    public UnitOfWork(NotificationDbContext context)
    {
        _context = context;
    }

    public INotificationMessageRepository NotificationMessageRepository =>
        _notificationMessageRepository ??= new NotificationMessageRepository(_context);

    public INotificationPreferenceRepository NotificationPreferenceRepository =>
        _notificationPreferenceRepository ??= new NotificationPreferenceRepository(_context);

    public INotificationTemplateRepository NotificationTemplateRepository =>
        _notificationTemplateRepository ??= new NotificationTemplateRepository(_context);

    public IPushSubscriptionRepository PushSubscriptionRepository =>
        _pushSubscriptionRepository ??= new PushSubscriptionRepository(_context);

    public IAdminNotificationRepository AdminNotificationRepository =>
        _adminNotificationRepository ??= new AdminNotificationRepository(_context);

    public INotificationInboxMessageRepository NotificationInboxMessageRepository =>
        _notificationInboxMessageRepository ??= new NotificationInboxMessageRepository(_context);

    public async Task<int> SaveChangesAsync()
    {
        return await _context.SaveChangesAsync();
    }

    public void Dispose()
    {
        _context.Dispose();
    }
}
