// =======================================================
// ITransactionRepository.cs
// =======================================================

using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Enums;

namespace WarpTalk.BillingService.Domain.Interfaces;

/// <summary>
/// Repository for billing transactions and payment operations
/// </summary>
public interface ITransactionRepository
{
    Task<Transaction?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    Task<Transaction?> GetByOrderCodeAsync(
        long orderCode,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Prevent duplicated payment callback processing
    /// </summary>
    Task<bool> ExistsByIdempotencyKeyAsync(
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get transactions of a workspace owner
    /// </summary>
    Task<IReadOnlyList<Transaction>> GetByOwnerUserIdAsync(
        Guid ownerUserId,
        int skip,
        int take,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get transactions of a workspace
    /// </summary>
    Task<IReadOnlyList<Transaction>> GetByWorkspaceIdAsync(
        Guid workspaceId,
        int skip,
        int take,
        CancellationToken cancellationToken = default);

    Task AddAsync(
        Transaction transaction,
        CancellationToken cancellationToken = default);

    Task UpdateAsync(
        Transaction transaction,
        CancellationToken cancellationToken = default);

    Task UpdateStatusAsync(
        Guid transactionId,
        TransactionStatus status,
        CancellationToken cancellationToken = default);
}