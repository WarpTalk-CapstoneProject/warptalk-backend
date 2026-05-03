// =======================================================
// IUnitOfWork.cs
// =======================================================

using System.Data;

namespace WarpTalk.BillingService.Domain.Interfaces;

/// <summary>
/// Unit of Work for atomic database transactions
/// </summary>
public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(
        CancellationToken cancellationToken = default);

    Task BeginTransactionAsync(
        CancellationToken cancellationToken = default);

    Task CommitAsync(
        CancellationToken cancellationToken = default);

    Task RollbackAsync(
        CancellationToken cancellationToken = default);

    IDbTransaction? GetCurrentTransaction();
}