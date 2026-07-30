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
    Task<T?> FirstOrDefaultAsync(Expression<Func<T, bool>> predicate, string includeProperties = "", CancellationToken ct = default);
    Task<T?> FirstOrDefaultAsync(Expression<Func<T, bool>> predicate, CancellationToken ct)
        => FirstOrDefaultAsync(predicate, "", ct);
    Task<bool> AnyAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default);
    Task<int> CountAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default);
    Task<IReadOnlyList<T>> GetPagedAsync(
        Expression<Func<T, bool>> predicate,
        int skip,
        int take,
        Func<IQueryable<T>, IQueryable<T>>? orderBy,
        CancellationToken ct = default);
    Task<IReadOnlyList<T>> GetPagedAsync(
        Expression<Func<T, bool>> predicate,
        int skip,
        int take,
        Func<IQueryable<T>, IQueryable<T>>? orderBy,
        Func<IQueryable<T>, IQueryable<T>>? include = null,
        CancellationToken cancellationToken = default)
        => GetPagedAsync(
            predicate,
            skip,
            take,
            query =>
            {
                query = include is null ? query : include(query);
                return orderBy is null ? query : orderBy(query);
            },
            cancellationToken);
    Task<IReadOnlyList<T>> GetPagedAsync(
        Expression<Func<T, bool>> predicate,
        int skip,
        int take,
        Func<IQueryable<T>, IQueryable<T>>? orderBy,
        Expression<Func<T, object>>[] includes,
        CancellationToken cancellationToken = default)
        => GetPagedAsync(
            predicate,
            skip,
            take,
            orderBy,
            query =>
            {
                foreach (var include in includes)
                    query = query.Provider.CreateQuery<T>(
                        Expression.Call(
                            typeof(Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions),
                            nameof(Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.Include),
                            new[] { typeof(T), typeof(object) },
                            query.Expression,
                            Expression.Quote(include)));
                return query;
            },
            cancellationToken);
    Task AddAsync(T entity, CancellationToken ct = default);
    void Update(T entity);
    void Remove(T entity);
    IQueryable<T> Query();
}
