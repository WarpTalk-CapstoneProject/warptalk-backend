using System.Linq.Expressions;

namespace WarpTalk.NotificationService.Domain.Interfaces;

public interface IGenericRepository<T> where T : class
{
    Task<T?> GetByIdAsync(Guid id);
    Task<IEnumerable<T>> GetAllAsync();
    Task<IEnumerable<T>> FindAsync(Expression<Func<T, bool>> predicate);
    Task AddAsync(T entity);
    void Update(T entity);
    void Remove(T entity);
    IQueryable<T> Query();
    Task<int> CountAsync(Expression<Func<T, bool>> predicate);
    Task<IEnumerable<T>> FindWithPaginationAsync(Expression<Func<T, bool>> predicate, int skip, int take, Func<IQueryable<T>, IOrderedQueryable<T>>? orderBy = null);
}
