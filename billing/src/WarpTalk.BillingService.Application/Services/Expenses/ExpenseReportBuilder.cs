using System;
using System.Collections.Generic;
using System.Linq;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.Shared.Contracts.Admin;

namespace WarpTalk.BillingService.Application.Services.Expenses;

/// <summary>
/// The expense report and the P&amp;L with expenses, from rows already read and converted (G12). Pure: the
/// clock and the data come in, so the arithmetic is tested without a database.
/// </summary>
public static class ExpenseReportBuilder
{
    /// <summary>A budget alert is raised from this share of the budget; over 100% it is an overrun.</summary>
    public const decimal AlertThresholdPercent = 80m;

    public const int TopVendorCount = 10;

    public static ExpenseReportDto Build(
        IReadOnlyList<DateOnly> months,
        IReadOnlyList<ExpenseCategoryDto> categories,
        IReadOnlyList<OperatingExpenseService.Converted> rows,
        IReadOnlyList<ExpenseBudget> budgets,
        IReadOnlyList<OperatingExpenseService.Converted> planned,
        IReadOnlyList<OperatingExpenseService.Converted> series,
        FxRateTable fx,
        DateOnly today,
        DateTime now)
    {
        var categoryById = categories.ToDictionary(c => c.Id);
        var budgetByCell = budgets.ToDictionary(b => (b.CategoryId, b.Month), b => b.AmountUsd);

        var monthRows = months.Select(month => MonthRow(month, rows, budgetByCell)).ToList();

        var categoryTotals = categories
            .Select(category =>
            {
                var own = rows.Where(r => r.Expense.CategoryId == category.Id).ToList();
                var budget = budgets.Where(b => b.CategoryId == category.Id).Select(b => b.AmountUsd).DefaultIfEmpty().Sum();
                return new ExpenseCategoryTotalDto(
                    category.Id, category.Slug, category.Name, category.Color,
                    own.Sum(r => r.Usd ?? 0), own.Count,
                    budgets.Any(b => b.CategoryId == category.Id) ? budget : null);
            })
            .Where(total => total.Count > 0 || total.BudgetUsd is not null)
            .OrderByDescending(total => total.AmountUsd)
            .ToList();

        var topVendors = rows
            .GroupBy(r => r.Expense.Vendor.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => new ExpenseVendorTotalDto(
                group.First().Expense.Vendor.Trim(),
                group.Sum(r => r.Usd ?? 0),
                group.Count(),
                group.GroupBy(r => r.Expense.CategoryId).OrderByDescending(g => g.Count()).Select(g => categoryById.GetValueOrDefault(g.Key)?.Name).FirstOrDefault()))
            .OrderByDescending(vendor => vendor.AmountUsd)
            .ThenBy(vendor => vendor.Vendor, StringComparer.OrdinalIgnoreCase)
            .Take(TopVendorCount)
            .ToList();

        var commitments = Commitments(planned, series, fx, today, categoryById);

        var alerts = new List<ExpenseBudgetAlertDto>();
        foreach (var month in monthRows)
        {
            foreach (var cell in month.Categories.Where(c => c.BudgetUsd is > 0))
            {
                var percent = Math.Round(cell.AmountUsd * 100m / cell.BudgetUsd!.Value, 1, MidpointRounding.AwayFromZero);
                if (percent < AlertThresholdPercent) continue;
                alerts.Add(new ExpenseBudgetAlertDto(
                    cell.CategoryId, categoryById.GetValueOrDefault(cell.CategoryId)?.Name ?? string.Empty,
                    month.Month, cell.BudgetUsd.Value, cell.AmountUsd, percent, percent > 100m));
            }
        }

        var total = rows.Sum(r => r.Usd ?? 0);
        var elapsed = months.Count(month => month <= ExpenseRecurrence.FirstOfMonth(today));
        var runRate = series
            .Where(s => s.Expense.RecurrenceEndDate is null || s.Expense.RecurrenceEndDate >= today)
            .Sum(s =>
            {
                var usd = OperatingExpenseService.ToUsd(s.Expense.Amount, s.Expense.Currency, today, fx).Usd ?? 0;
                return s.Expense.Recurrence == OperatingExpenseConstants.Recurrences.Yearly ? usd / 12m : usd;
            });

        return new ExpenseReportDto(
            ExpenseRecurrence.MonthKey(months[0]),
            ExpenseRecurrence.MonthKey(months[^1]),
            now,
            categories,
            monthRows,
            categoryTotals,
            topVendors,
            commitments,
            alerts.OrderByDescending(a => a.Month, StringComparer.Ordinal).ThenByDescending(a => a.Percent).ToList(),
            total,
            rows.Where(r => r.Expense.Status == OperatingExpenseConstants.Statuses.Paid).Sum(r => r.Usd ?? 0),
            rows.Where(r => r.Expense.Status == OperatingExpenseConstants.Statuses.Planned).Sum(r => r.Usd ?? 0),
            elapsed == 0 ? 0 : decimal.Round(total / elapsed, 0, MidpointRounding.AwayFromZero),
            decimal.Round(runRate, 0, MidpointRounding.AwayFromZero),
            rows.Count(r => r.Usd is null),
            OperatingExpenseService.Describe(rows));
    }

    public static ExpenseReportMonthDto MonthRow(
        DateOnly month,
        IReadOnlyList<OperatingExpenseService.Converted> rows,
        IReadOnlyDictionary<(Guid CategoryId, DateOnly Month), decimal> budgetByCell)
    {
        var own = rows.Where(r => ExpenseRecurrence.FirstOfMonth(r.Expense.ExpenseDate) == month).ToList();
        var categoryIds = own.Select(r => r.Expense.CategoryId)
            .Concat(budgetByCell.Keys.Where(k => k.Month == month).Select(k => k.CategoryId))
            .Distinct();
        var cells = categoryIds
            .Select(id =>
            {
                var amount = own.Where(r => r.Expense.CategoryId == id).Sum(r => r.Usd ?? 0);
                decimal? budget = budgetByCell.TryGetValue((id, month), out var b) ? b : null;
                return new ExpenseMonthCategoryDto(id, amount, budget, budget is { } cap && amount > cap);
            })
            .OrderByDescending(c => c.AmountUsd)
            .ToList();
        var budgets = budgetByCell.Where(kv => kv.Key.Month == month).Select(kv => kv.Value).ToList();

        return new ExpenseReportMonthDto(
            ExpenseRecurrence.MonthKey(month),
            own.Sum(r => r.Usd ?? 0),
            own.Where(r => r.Expense.Status == OperatingExpenseConstants.Statuses.Paid).Sum(r => r.Usd ?? 0),
            own.Where(r => r.Expense.Status == OperatingExpenseConstants.Statuses.Planned).Sum(r => r.Usd ?? 0),
            budgets.Count == 0 ? null : budgets.Sum(),
            cells);
    }

    private static List<ExpenseCommitmentDto> Commitments(
        IReadOnlyList<OperatingExpenseService.Converted> planned,
        IReadOnlyList<OperatingExpenseService.Converted> series,
        FxRateTable fx,
        DateOnly today,
        IReadOnlyDictionary<Guid, ExpenseCategoryDto> categories)
    {
        var horizon = today.AddDays(OperatingExpenseConstants.CommitmentHorizonDays);
        var list = new List<ExpenseCommitmentDto>();

        foreach (var row in planned.Where(r => r.Expense.ExpenseDate <= horizon))
        {
            var e = row.Expense;
            list.Add(new ExpenseCommitmentDto(
                e.Id, e.RecurringSourceId, e.ExpenseDate, e.Vendor, e.CategoryId, categories.GetValueOrDefault(e.CategoryId)?.Name ?? string.Empty,
                e.Amount, e.Currency, row.Usd,
                e.RecurringSourceId is null ? OperatingExpenseConstants.Recurrences.None : "occurrence", Projected: false));
        }

        // Occurrences the worker has not written yet: from each series' next due date to the horizon.
        foreach (var s in series)
        {
            var e = s.Expense;
            if (e.NextDueDate is not { } next) continue;
            foreach (var date in ExpenseRecurrence.Upcoming(e.ExpenseDate, e.Recurrence, next, horizon, e.RecurrenceEndDate))
            {
                list.Add(new ExpenseCommitmentDto(
                    null, e.Id, date, e.Vendor, e.CategoryId, categories.GetValueOrDefault(e.CategoryId)?.Name ?? string.Empty,
                    e.Amount, e.Currency, OperatingExpenseService.ToUsd(e.Amount, e.Currency, date, fx).Usd,
                    e.Recurrence, Projected: true));
            }
        }

        return list.OrderBy(c => c.DueDate).ThenBy(c => c.Vendor, StringComparer.OrdinalIgnoreCase).ToList();
    }
}

/// <summary>Revenue and AI cost from the Insights P&amp;L, plus operating expenses, per local month (G12).</summary>
public static class FinancePnlBuilder
{
    public static FinancePnlDto Build(
        IReadOnlyList<DateOnly> months,
        AdminProfitAndLossDto pnl,
        IReadOnlyList<ExpenseCategoryDto> categories,
        IReadOnlyList<OperatingExpenseService.Converted> rows,
        IReadOnlyList<ExpenseBudget> budgets,
        DateTime now)
    {
        var budgetByCell = budgets.ToDictionary(b => (b.CategoryId, b.Month), b => b.AmountUsd);
        var dayByMonth = pnl.Days.GroupBy(day => day.Key[..7], StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var monthRows = new List<FinancePnlMonthDto>();
        foreach (var month in months)
        {
            var key = ExpenseRecurrence.MonthKey(month);
            var days = dayByMonth.GetValueOrDefault(key) ?? [];
            var expense = ExpenseReportBuilder.MonthRow(month, rows, budgetByCell);
            monthRows.Add(Row(key, days, expense.TotalUsd, expense.Categories));
        }

        var allDays = pnl.Days.Where(day => months.Any(m => day.Key.StartsWith(ExpenseRecurrence.MonthKey(m), StringComparison.Ordinal))).ToList();
        var byCategory = monthRows
            .SelectMany(m => m.ExpensesByCategory)
            .GroupBy(c => c.CategoryId)
            .Select(g => new ExpenseMonthCategoryDto(g.Key, g.Sum(c => c.AmountUsd), null, false))
            .OrderByDescending(c => c.AmountUsd)
            .ToList();
        var total = Row("total", allDays, monthRows.Sum(m => m.OperatingExpenses), byCategory);

        return new FinancePnlDto(
            ExpenseRecurrence.MonthKey(months[0]),
            ExpenseRecurrence.MonthKey(months[^1]),
            now,
            monthRows,
            total,
            categories,
            pnl.CostNote,
            pnl.FxNote,
            OperatingExpenseService.Describe(rows));
    }

    private static FinancePnlMonthDto Row(string key, IReadOnlyList<AdminPnlPeriodDto> days, decimal operatingExpenses, IReadOnlyList<ExpenseMonthCategoryDto> byCategory)
    {
        var revenue = SumKnown(days.Select(d => d.Revenue));
        var aiCost = SumKnown(days.Select(d => d.AiCost));
        decimal? gross = revenue is { } r && aiCost is { } c ? r - c : null;
        decimal? grossPercent = gross is { } g && revenue is > 0 ? Math.Round(g * 100m / revenue.Value, 1, MidpointRounding.AwayFromZero) : null;
        decimal? net = gross is { } gm ? gm - operatingExpenses : null;
        decimal? netPercent = net is { } n && revenue is > 0 ? Math.Round(n * 100m / revenue.Value, 1, MidpointRounding.AwayFromZero) : null;
        var credits = days.Sum(d => d.Credits);
        var coverage = credits <= 0
            ? 100m
            : Math.Round(days.Sum(d => d.CostCoveragePercent * d.Credits) / credits, 1, MidpointRounding.AwayFromZero);

        return new FinancePnlMonthDto(key, revenue, aiCost, gross, grossPercent, operatingExpenses, net, netPercent, byCategory, coverage);
    }

    /// <summary>
    /// The sum of the known figures; null only when there are days and none of them is known. A day with
    /// no figure (no rate, no reconstructable cost) is left out rather than counted as 0 — the P&amp;L's
    /// CostNote/FxNote say so.
    /// </summary>
    private static decimal? SumKnown(IEnumerable<decimal?> values)
    {
        var list = values.ToList();
        if (list.Count == 0) return 0m;
        var known = list.Where(v => v.HasValue).Select(v => v!.Value).ToList();
        return known.Count == 0 ? null : known.Sum();
    }
}
