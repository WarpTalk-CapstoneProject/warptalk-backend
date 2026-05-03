using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Enums;

namespace WarpTalk.BillingService.Application.Services.Interface
{
    public interface ITransactionService
    {
        Task<Transaction> CreateAsync(CreateTransactionCommand command, CancellationToken ct = default);

        Task<Transaction?> GetByIdAsync(Guid id, CancellationToken ct = default);

        Task<Transaction?> GetByOrderCodeAsync(long orderCode, CancellationToken ct = default);

        Task<IReadOnlyList<Transaction>> GetByWorkspaceAsync(
            Guid workspaceId,
            int page,
            int pageSize,
            CancellationToken ct = default);

        Task UpdateStatusAsync(Guid id, TransactionStatus status, CancellationToken ct = default);
    }
}
