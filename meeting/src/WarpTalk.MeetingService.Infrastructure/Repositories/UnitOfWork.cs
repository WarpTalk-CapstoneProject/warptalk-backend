using WarpTalk.MeetingService.Domain.Interfaces;
using WarpTalk.MeetingService.Infrastructure.Persistence;

namespace WarpTalk.MeetingService.Infrastructure.Repositories;

public class UnitOfWork : IUnitOfWork
{
    private readonly MeetingDbContext _context;
    private readonly Dictionary<Type, object> _repositories = new();

    public UnitOfWork(MeetingDbContext context) => _context = context;

    public IGenericRepository<T> Repository<T>() where T : class
    {
        var type = typeof(T);
        if (!_repositories.ContainsKey(type))
        {
            var repositoryInstance = new GenericRepository<T>(_context);
            _repositories.Add(type, repositoryInstance);
        }
        return (IGenericRepository<T>)_repositories[type];
    }

    public async Task<int> SaveChangesAsync(CancellationToken ct = default) => await _context.SaveChangesAsync(ct);

    public void Dispose() => _context.Dispose();
}
