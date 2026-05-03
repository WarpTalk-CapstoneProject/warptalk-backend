// =======================================================
// ICreditLedgerRepository.cs
// =======================================================

using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Domain.Interfaces;

public interface ICreditLedgerRepository
{
    Task AddAsync(
        CreditLedgerEntry entry,
        CancellationToken cancellationToken = default);

    Task<decimal> GetWorkspaceBalanceAsync(
        Guid workspaceId,
        CancellationToken cancellationToken = default);

    Task<bool> ExistsByIdempotencyKeyAsync(
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CreditLedgerEntry>> GetWorkspaceLedgerAsync(
        Guid workspaceId,
        int skip,
        int take,
        CancellationToken cancellationToken = default);
}