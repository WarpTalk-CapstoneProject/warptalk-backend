using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Domain.Interfaces;

/// <summary>subscription.expense_categories (G12).</summary>
public interface IExpenseCategoryRepository : IGenericRepository<ExpenseCategory>
{
    /// <summary>Every category, active or not, in display order.</summary>
    Task<IReadOnlyList<ExpenseCategory>> ListOrderedAsync(CancellationToken ct = default);

    Task<ExpenseCategory?> GetBySlugAsync(string slug, CancellationToken ct = default);

    Task<bool> IsInUseAsync(System.Guid categoryId, CancellationToken ct = default);
}
