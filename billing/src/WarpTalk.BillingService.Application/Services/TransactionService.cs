using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Services.Interface;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Enums;
using WarpTalk.BillingService.Domain.Interfaces;

namespace WarpTalk.BillingService.Application.Services;

public class TransactionService : ITransactionService
{
    private readonly ITransactionRepository _repo;
    private readonly IUnitOfWork _uow;

    public TransactionService(
        ITransactionRepository repo,
        IUnitOfWork uow)
    {
        _repo = repo;
        _uow = uow;
    }

    // ======================================================
    // CREATE TRANSACTION
    // ======================================================
    public async Task<Transaction> CreateAsync(
        CreateTransactionCommand command,
        CancellationToken ct = default)
    {
        var transaction = new Transaction
        {
            Id = Guid.NewGuid(),
            WorkspaceId = command.WorkspaceId,
            OwnerUserId = command.OwnerUserId,
            PlanId = command.PlanId,
            OrderCode = command.OrderCode,
            AmountVnd = command.AmountVnd,
            Status = TransactionStatus.Pending,
            CreatedAt = DateTime.UtcNow
        };

        await _repo.AddAsync(transaction, ct);
        await _uow.SaveChangesAsync(ct);

        return transaction;
    }

    // ======================================================
    // GET BY ID
    // ======================================================
    public Task<Transaction?> GetByIdAsync(
        Guid id,
        CancellationToken ct = default)
    {
        return _repo.GetByIdAsync(id, ct);
    }

    // ======================================================
    // GET BY ORDER CODE
    // ======================================================
    public Task<Transaction?> GetByOrderCodeAsync(
        long orderCode,
        CancellationToken ct = default)
    {
        return _repo.GetByOrderCodeAsync(orderCode, ct);
    }

    // ======================================================
    // GET BY WORKSPACE
    // ======================================================
    public Task<IReadOnlyList<Transaction>> GetByWorkspaceAsync(
        Guid workspaceId,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        var skip = (page - 1) * pageSize;

        return _repo.GetByWorkspaceIdAsync(
            workspaceId,
            skip,
            pageSize,
            ct);
    }

    // ======================================================
    // UPDATE STATUS
    // ======================================================
    public async Task UpdateStatusAsync(
        Guid id,
        TransactionStatus status,
        CancellationToken ct = default)
    {
        var tx = await _repo.GetByIdAsync(id, ct);
        if (tx == null) return;

        tx.Status = status;

        if (status == TransactionStatus.Success ||
            status == TransactionStatus.Failed)
        {
            tx.CompletedAt = DateTime.UtcNow;
        }

        await _repo.UpdateAsync(tx, ct);
        await _uow.SaveChangesAsync(ct);
    }
}