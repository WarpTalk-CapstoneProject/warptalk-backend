using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Domain.Interfaces;

public interface ICreditTransactionRepository : IGenericRepository<CreditTransaction>
{
    Task<PagedResult<CreditTransaction>> GetHistoryPageAsync(CreditTransactionHistoryFilter filter, CancellationToken cancellationToken = default);
    Task<CreditTransaction?> GetLatestBeforeAsync(Guid subscriptionId, DateTime before, CancellationToken cancellationToken = default);

    // ── Admin Insights (2026-09-17). Consume transactions only; soft-deleted subscriptions included. ──

    /// <summary>Consumed credits, derived overage and reconstructable provider cost for [from, to).</summary>
    Task<ConsumptionTotals> GetConsumptionTotalsAsync(DateTime from, DateTime to, CancellationToken cancellationToken = default);

    /// <summary>Consumed credits in [from, to) per charge type (usage type when the charge type is empty), largest first.</summary>
    Task<IReadOnlyList<CreditsByChargeType>> GetConsumedByChargeTypeAsync(DateTime from, DateTime to, CancellationToken cancellationToken = default);

    /// <summary>Workspaces consuming at least <paramref name="minCredits"/> in [from, to), largest first.</summary>
    Task<IReadOnlyList<WorkspaceCredits>> GetTopConsumingWorkspacesAsync(
        DateTime from, DateTime to, int take, long minCredits = 1, CancellationToken cancellationToken = default);
}
