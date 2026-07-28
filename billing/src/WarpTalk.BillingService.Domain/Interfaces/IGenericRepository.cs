using System.Linq.Expressions;

namespace WarpTalk.BillingService.Domain.Interfaces;

public interface IGenericRepository<T> where T : class
{
    Task<T?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<T>> GetAllAsync(string includeProperties = "", CancellationToken ct = default);
    Task<IReadOnlyList<T>> GetAllAsync(CancellationToken ct)
        => GetAllAsync("", ct);
    Task<IReadOnlyList<T>> FindAsync(Expression<Func<T, bool>> predicate, string includeProperties = "", CancellationToken ct = default);
    Task<IReadOnlyList<T>> FindAsync(Expression<Func<T, bool>> predicate, CancellationToken ct)
        => FindAsync(predicate, "", ct);
    Task<IReadOnlyList<T>> GetPagedAsync(
        Expression<Func<T, bool>> predicate,
        int skip,
        int take,
        Func<IQueryable<T>, IQueryable<T>>? orderBy = null,
        CancellationToken ct = default);
    Task<T?> FirstOrDefaultAsync(Expression<Func<T, bool>> predicate, string includeProperties = "", CancellationToken ct = default);
    Task<T?> FirstOrDefaultAsync(Expression<Func<T, bool>> predicate, CancellationToken ct)
        => FirstOrDefaultAsync(predicate, "", ct);
    Task<bool> AnyAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default);
    Task<int> CountAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default);
    Task AddAsync(T entity, CancellationToken ct = default);
    void Update(T entity);
    void Remove(T entity);
}
