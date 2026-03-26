using WarpTalk.NotificationService.Domain.Interfaces;
using WarpTalk.NotificationService.Infrastructure.Persistence;

namespace WarpTalk.NotificationService.Infrastructure.Repositories;

public class UnitOfWork : IUnitOfWork
{
    private readonly NotificationDbContext _context;
    private readonly Dictionary<Type, object> _repositories;

    public UnitOfWork(NotificationDbContext context)
    {
        _context = context;
        _repositories = new Dictionary<Type, object>();
    }

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
