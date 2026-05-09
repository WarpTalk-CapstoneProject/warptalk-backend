using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using WarpTalk.NotificationService.Domain.Interfaces;
using WarpTalk.NotificationService.Infrastructure.Persistence;

namespace WarpTalk.NotificationService.Infrastructure.Repositories;

public class GenericRepository<T> : IGenericRepository<T> where T : class
{
    protected readonly NotificationDbContext _context;
    protected readonly DbSet<T> _dbSet;

    public GenericRepository(NotificationDbContext context)
    {
        _context = context;
        _dbSet = context.Set<T>();
    }

    public async Task<T?> GetByIdAsync(Guid id)
    {
        return await _dbSet.FindAsync(id);
    }

    public async Task<IEnumerable<T>> GetAllAsync()
    {
        return await _dbSet.ToListAsync();
    }

    public async Task<IEnumerable<T>> FindAsync(Expression<Func<T, bool>> predicate)
    {
        return await _dbSet.Where(predicate).ToListAsync();
    }

    public async Task AddAsync(T entity)
    {
        await _dbSet.AddAsync(entity);
    }

    public async Task AddRangeAsync(IEnumerable<T> entities)
    {
        await _dbSet.AddRangeAsync(entities);
    }

    public void Update(T entity)
    {
        _dbSet.Update(entity);
    }

    public void Remove(T entity)
    {
        _dbSet.Remove(entity);
    }

    public IQueryable<T> Query()
    {
        return _dbSet.AsQueryable();
    }

    public async Task<int> CountAsync(Expression<Func<T, bool>> predicate)
    {
        return await _dbSet.CountAsync(predicate);
    }

    public async Task<IEnumerable<T>> FindWithPaginationAsync(Expression<Func<T, bool>> predicate, int skip, int take, Func<IQueryable<T>, IOrderedQueryable<T>>? orderBy = null)
    {
        var query = _dbSet.Where(predicate);
        if (orderBy != null)
        {
            query = orderBy(query);
        }
        return await query.Skip(skip).Take(take).ToListAsync();
    }
}
