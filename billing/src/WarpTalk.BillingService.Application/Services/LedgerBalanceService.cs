using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Application.Services;

public interface ILedgerBalanceService
{
    Task<int> CalculateBalanceAsync(Guid subscriptionId, CancellationToken cancellationToken = default);
}

public class LedgerBalanceService : ILedgerBalanceService
{
    private readonly IUnitOfWork _unitOfWork;

    public LedgerBalanceService(IUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public async Task<int> CalculateBalanceAsync(Guid subscriptionId, CancellationToken cancellationToken = default)
    {
        // Get the latest snapshot
        var snapshots = await _unitOfWork.CreditBalanceSnapshotRepository.GetPagedAsync(
            predicate: s => s.SubscriptionId == subscriptionId,
            skip: 0,
            take: 1,
            orderBy: q => q.OrderByDescending(s => s.SnapshotAt),
            cancellationToken: cancellationToken);

        var snapshot = snapshots.FirstOrDefault();

        var baseBalance = snapshot?.CreditsRemaining ?? 0;
        var fromDate = snapshot?.SnapshotAt ?? DateTime.MinValue;

        // Get all relevant ledger entries since the snapshot
        var entries = await _unitOfWork.CreditTransactionRepository
            .FindAsync(tx => tx.SubscriptionId == subscriptionId && tx.CreatedAt >= fromDate && tx.Status != "rolled_back", cancellationToken);

        var netChange = 0;

        foreach (var entry in entries)
        {
            switch (entry.Type.ToLower())
            {
                case "top_up":
                case "refund":
                case "adjustment":
                    netChange += entry.Amount;
                    break;

                case "consume":
                    netChange -= entry.Amount;
                    break;

                case "reserve":
                    if (entry.Status == "pending")
                    {
                        netChange -= entry.Amount;
                    }
                    break;
            }
        }

        return baseBalance + netChange;
    }
}
