using Microsoft.EntityFrameworkCore;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Infrastructure.Persistence;

namespace WarpTalk.BillingService.Infrastructure.Repositories;

public class ExpenseBudgetRepository : GenericRepository<ExpenseBudget>, IExpenseBudgetRepository
{
    public ExpenseBudgetRepository(BillingDbContext db) : base(db)
    {
    }

    public async Task<IReadOnlyList<ExpenseBudget>> ListInRangeAsync(DateOnly fromMonth, DateOnly toMonth, CancellationToken ct = default)
        => await _context.ExpenseBudgets.AsNoTracking()
            .Where(budget => budget.Month >= fromMonth && budget.Month <= toMonth)
            .ToListAsync(ct);

    public Task<ExpenseBudget?> GetAsync(Guid categoryId, DateOnly month, CancellationToken ct = default)
        => _context.ExpenseBudgets.FirstOrDefaultAsync(budget => budget.CategoryId == categoryId && budget.Month == month, ct);
}
