using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using WarpTalk.AuthService.Domain.Interfaces;
using WarpTalk.AuthService.Infrastructure.Persistence;

namespace WarpTalk.AuthService.Infrastructure.Repositories;

public class GenericRepository<T> : IGenericRepository<T> where T : class
{
    private readonly AuthDbContext _db;
    private readonly DbSet<T> _set;

    public GenericRepository(AuthDbContext db)
    {
        _db = db;
        _set = db.Set<T>();
    }

    public async Task<T?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => await _set.FindAsync([id], ct);

    public async Task<IReadOnlyList<T>> GetAllAsync(string includeProperties = "", CancellationToken ct = default)
    {
        IQueryable<T> query = _set;
        foreach (var includeProperty in includeProperties.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            query = query.Include(includeProperty.Trim());
        return await query.ToListAsync(ct);
    }

    public async Task<IReadOnlyList<T>> FindAsync(Expression<Func<T, bool>> predicate, string includeProperties = "", CancellationToken ct = default)
    {
        IQueryable<T> query = _set;
        foreach (var includeProperty in includeProperties.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            query = query.Include(includeProperty.Trim());
        return await query.Where(predicate).ToListAsync(ct);
    }

    public async Task<T?> FirstOrDefaultAsync(Expression<Func<T, bool>> predicate, string includeProperties = "", CancellationToken ct = default)
    {
        IQueryable<T> query = _set;
        foreach (var includeProperty in includeProperties.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            query = query.Include(includeProperty.Trim());
        return await query.FirstOrDefaultAsync(predicate, ct);
    }

    public async Task<bool> AnyAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default)
        => await _set.AnyAsync(predicate, ct);

    public async Task AddAsync(T entity, CancellationToken ct = default)
        => await _set.AddAsync(entity, ct);

    public void Update(T entity) => _set.Update(entity);

    public void Remove(T entity) => _set.Remove(entity);

    public IQueryable<T> Query() => _set.AsQueryable();
}
