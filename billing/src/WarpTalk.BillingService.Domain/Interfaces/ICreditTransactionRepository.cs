using System;
using System.Collections.Generic;
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

    /// <summary>
    /// <see cref="GetConsumptionTotalsAsync"/> for one workspace — the rate-card provider cost of
    /// what THIS workspace consumed. Same coverage rule, same charge-type key.
    /// </summary>
    Task<ConsumptionTotals> GetWorkspaceConsumptionTotalsAsync(
        Guid workspaceId, DateTime from, DateTime to, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every ledger row of one workspace in [from, to) as (when, amount, balance after), oldest
    /// first — the input of the credit burn chart. Three scalars per row, projected, never a record
    /// the database is asked to shape.
    /// </summary>
    Task<IReadOnlyList<LedgerPoint>> GetWorkspaceLedgerPointsAsync(
        Guid workspaceId, DateTime from, DateTime to, CancellationToken cancellationToken = default);

    /// <summary>Consume rows in [from, to) of the given charge types, per UTC day and charge type.</summary>
    Task<IReadOnlyList<DailyChargeTypeConsumption>> GetConsumptionByUtcDayAsync(
        DateTime from, DateTime to, IReadOnlyCollection<string> chargeTypes, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CreditsByChargeType>> GetConsumedByChargeTypeAsync(DateTime from, DateTime to, CancellationToken cancellationToken = default);

    /// <summary>WT-692: distinct workspaces with at least one consume row in [from, to).</summary>
    Task<int> CountConsumingWorkspacesAsync(DateTime from, DateTime to, CancellationToken cancellationToken = default);

    /// <summary>Workspaces consuming at least <paramref name="minCredits"/> in [from, to), largest first.</summary>
    Task<IReadOnlyList<WorkspaceCredits>> GetTopConsumingWorkspacesAsync(
        DateTime from, DateTime to, int take, long minCredits = 1, CancellationToken cancellationToken = default);

    // ── Profit and loss (2026-09-24) ──

    /// <summary>Consume rows in [from, to) per UTC half-hour, charge type, provider and plan, with provider cost.</summary>
    Task<IReadOnlyList<ConsumptionSlotRow>> GetConsumptionSlotsAsync(DateTime from, DateTime to, CancellationToken cancellationToken = default);

    /// <summary>Consumed credits in [from, to) per UTC half-hour, workspace and plan.</summary>
    Task<IReadOnlyList<WorkspaceSlotRow>> GetWorkspaceSlotsAsync(DateTime from, DateTime to, CancellationToken cancellationToken = default);
}
