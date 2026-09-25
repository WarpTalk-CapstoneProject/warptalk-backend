using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Domain.Interfaces;

/// <summary>subscription.expense_budgets (G12).</summary>
public interface IExpenseBudgetRepository : IGenericRepository<ExpenseBudget>
{
    /// <summary>Budgets of the months <c>from..to</c> (first days of month, inclusive). Untracked.</summary>
    Task<IReadOnlyList<ExpenseBudget>> ListInRangeAsync(DateOnly fromMonth, DateOnly toMonth, CancellationToken ct = default);

    /// <summary>The budget of one (category, month), tracked; null when none is set.</summary>
    Task<ExpenseBudget?> GetAsync(Guid categoryId, DateOnly month, CancellationToken ct = default);
}
