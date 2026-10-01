using Microsoft.EntityFrameworkCore;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Infrastructure.Persistence;

namespace WarpTalk.BillingService.Infrastructure.Repositories;

public class OperatingExpenseRepository : GenericRepository<OperatingExpense>, IOperatingExpenseRepository
{
    public OperatingExpenseRepository(BillingDbContext db) : base(db)
    {
    }

    private IQueryable<OperatingExpense> Live => _context.OperatingExpenses.Where(expense => expense.DeletedAt == null);

    public async Task<IReadOnlyList<OperatingExpense>> ListInRangeAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
        => await Live.AsNoTracking()
            .Include(expense => expense.Category)
            .Where(expense => expense.ExpenseDate >= from && expense.ExpenseDate <= to)
            .OrderBy(expense => expense.ExpenseDate)
            .ThenBy(expense => expense.CreatedAt)
            .ToListAsync(ct);

    public Task<OperatingExpense?> GetLiveAsync(Guid id, CancellationToken ct = default)
        => Live.Include(expense => expense.Category).FirstOrDefaultAsync(expense => expense.Id == id, ct);

    public async Task<IReadOnlyList<OperatingExpense>> ListSeriesAsync(CancellationToken ct = default)
        => await Live.AsNoTracking()
            .Include(expense => expense.Category)
            .Where(expense => expense.Recurrence != OperatingExpenseConstants.Recurrences.None)
            .OrderBy(expense => expense.NextDueDate)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<OperatingExpense>> ListSeriesDueAsync(DateOnly dueBy, CancellationToken ct = default)
        => await Live
            .Where(expense => expense.Recurrence != OperatingExpenseConstants.Recurrences.None
                && expense.NextDueDate != null
                && expense.NextDueDate <= dueBy)
            .OrderBy(expense => expense.NextDueDate)
            .ToListAsync(ct);

    public Task<bool> OccurrenceExistsAsync(Guid seriesId, DateOnly date, CancellationToken ct = default)
        => _context.OperatingExpenses.AnyAsync(
            expense => expense.RecurringSourceId == seriesId && expense.ExpenseDate == date, ct);

    public async Task<IReadOnlyList<OperatingExpense>> ListPlannedDueAsync(DateOnly dueBy, CancellationToken ct = default)
        => await Live.AsNoTracking()
            .Include(expense => expense.Category)
            .Where(expense => expense.Status == OperatingExpenseConstants.Statuses.Planned && expense.ExpenseDate <= dueBy)
            .OrderBy(expense => expense.ExpenseDate)
            .ToListAsync(ct);
}
