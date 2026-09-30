using Microsoft.EntityFrameworkCore;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Infrastructure.Persistence;

namespace WarpTalk.BillingService.Infrastructure.Repositories;

public class ExpenseCategoryRepository : GenericRepository<ExpenseCategory>, IExpenseCategoryRepository
{
    public ExpenseCategoryRepository(BillingDbContext db) : base(db)
    {
    }

    public async Task<IReadOnlyList<ExpenseCategory>> ListOrderedAsync(CancellationToken ct = default)
        => await _context.ExpenseCategories.AsNoTracking()
            .OrderBy(category => category.SortOrder)
            .ThenBy(category => category.Name)
            .ToListAsync(ct);

    public Task<ExpenseCategory?> GetBySlugAsync(string slug, CancellationToken ct = default)
        => _context.ExpenseCategories.FirstOrDefaultAsync(category => category.Slug == slug, ct);

    public Task<bool> IsInUseAsync(Guid categoryId, CancellationToken ct = default)
        => _context.OperatingExpenses.AnyAsync(expense => expense.CategoryId == categoryId, ct);
}
