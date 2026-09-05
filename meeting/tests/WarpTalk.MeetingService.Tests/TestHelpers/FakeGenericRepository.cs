using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.MeetingService.Domain.Interfaces;

namespace WarpTalk.MeetingService.Tests.TestHelpers;

/// <summary>
/// Minimal in-memory IGenericRepository&lt;T&gt; test double, backed by a plain List&lt;T&gt;.
/// Supports repository-backed service tests without hand-writing every CRUD setup.
/// </summary>
public class FakeGenericRepository<T> : IGenericRepository<T> where T : class
{
    public List<T> Items { get; } = new();

    public Task<T?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        throw new NotSupportedException("Not used by the service under test.");

    public Task<IReadOnlyList<T>> GetAllAsync(string includeProperties = "", CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<T>>(Items.ToList());

    public Task<IReadOnlyList<T>> FindAsync(Expression<Func<T, bool>> predicate, string includeProperties = "", CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<T>>(Items.Where(predicate.Compile()).ToList());

    public Task<T?> FirstOrDefaultAsync(Expression<Func<T, bool>> predicate, string includeProperties = "", CancellationToken ct = default) =>
        Task.FromResult(Items.FirstOrDefault(predicate.Compile()));

    public Task<bool> AnyAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default) =>
        Task.FromResult(Items.Any(predicate.Compile()));

    public Task AddAsync(T entity, CancellationToken ct = default)
    {
        Items.Add(entity);
        return Task.CompletedTask;
    }

    public void Update(T entity)
    {
        // Entities under test are reference types mutated in place — nothing to persist.
    }

    public void Remove(T entity) => Items.Remove(entity);

    public IQueryable<T> Query() => Items.AsQueryable();
}
