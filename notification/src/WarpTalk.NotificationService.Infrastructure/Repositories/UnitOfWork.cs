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
    private INotificationTemplateVersionRepository? _notificationTemplateVersionRepository;
    private IAnnouncementRepository? _announcementRepository;
    private IAnnouncementDismissalRepository? _announcementDismissalRepository;

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

    public INotificationTemplateVersionRepository NotificationTemplateVersionRepository =>
        _notificationTemplateVersionRepository ??= new NotificationTemplateVersionRepository(_context);

    public IAnnouncementRepository AnnouncementRepository =>
        _announcementRepository ??= new AnnouncementRepository(_context);

    public IAnnouncementDismissalRepository AnnouncementDismissalRepository =>
        _announcementDismissalRepository ??= new AnnouncementDismissalRepository(_context);

    private Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? _currentTransaction;

    public async Task<int> SaveChangesAsync()
    {
        return await _context.SaveChangesAsync();
    }

    public async Task BeginTransactionAsync(CancellationToken ct = default)
    {
        _currentTransaction = await _context.Database.BeginTransactionAsync(ct);
    }

    public async Task CommitTransactionAsync(CancellationToken ct = default)
    {
        if (_currentTransaction != null)
        {
            await _currentTransaction.CommitAsync(ct);
            await _currentTransaction.DisposeAsync();
            _currentTransaction = null;
        }
    }

    public async Task RollbackTransactionAsync(CancellationToken ct = default)
    {
        if (_currentTransaction != null)
        {
            await _currentTransaction.RollbackAsync(ct);
            await _currentTransaction.DisposeAsync();
            _currentTransaction = null;
        }
    }

    public void Dispose()
    {
        _currentTransaction?.Dispose();
        _context.Dispose();
    }
}
