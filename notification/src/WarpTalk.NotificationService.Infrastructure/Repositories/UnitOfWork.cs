using WarpTalk.NotificationService.Domain.Interfaces;
using WarpTalk.NotificationService.Infrastructure.Persistence;

namespace WarpTalk.NotificationService.Infrastructure.Repositories;

public class UnitOfWork : IUnitOfWork
{
    private readonly NotificationDbContext _context;
    private readonly Dictionary<Type, object> _repositories;
    private INotificationMessageRepository? _notificationMessageRepository;
    private INotificationPreferenceRepository? _notificationPreferenceRepository;
    private INotificationTemplateRepository? _notificationTemplateRepository;
    private IPushSubscriptionRepository? _pushSubscriptionRepository;
    private IAdminNotificationRepository? _adminNotificationRepository;

    public UnitOfWork(NotificationDbContext context)
    {
        _context = context;
        _repositories = new Dictionary<Type, object>();
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

    public IGenericRepository<T> Repository<T>() where T : class
    {
        var type = typeof(T);

        if (!_repositories.ContainsKey(type))
        {
            var repositoryType = typeof(GenericRepository<>);
            var repositoryInstance = Activator.CreateInstance(repositoryType.MakeGenericType(typeof(T)), _context);
            if (repositoryInstance != null)
            {
                _repositories.Add(type, repositoryInstance);
            }
        }

        return (IGenericRepository<T>)_repositories[type];
    }

    public async Task<int> SaveChangesAsync()
    {
        return await _context.SaveChangesAsync();
    }

    public void Dispose()
    {
        _context.Dispose();
    }
}
