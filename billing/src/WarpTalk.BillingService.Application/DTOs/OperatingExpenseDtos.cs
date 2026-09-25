using System;
using System.Collections.Generic;

namespace WarpTalk.BillingService.Application.DTOs;

// G12 — internal management: operating costs and expenses. camelCase on the wire; these names ARE
// the contract with warptalk-web src/types/admin-expenses.ts.
//
// Money: an expense keeps its own currency (VND or USD). Every VND figure below is converted at the
// USD→VND rate of the expense's own date (subscription.fx_rates, like the Insights P&L) and is null
// with a note when no rate exists. Months are "yyyy-MM", dates "yyyy-MM-dd".

public sealed record ExpenseCategoryDto(
    Guid Id,
    string Slug,
    string Name,
    string? Description,
    string? Color,
    int SortOrder,
    bool IsActive,
    bool InUse);

public sealed record SaveExpenseCategoryRequest(
    string Name,
    string? Slug,
    string? Description,
    string? Color,
    int? SortOrder,
    bool? IsActive);

public sealed record ExpenseReceiptDto(string FileName, string? ContentType, long? SizeBytes);

public sealed record OperatingExpenseDto(
    Guid Id,
    DateOnly ExpenseDate,
    string Vendor,
    Guid CategoryId,
    string CategorySlug,
    string CategoryName,
    string? CategoryColor,
    string? Description,
    decimal Amount,
    string Currency,
    decimal? AmountVnd,
    decimal? FxRate,
    string? FxSource,
    DateOnly? FxRateDate,
    string? PaymentMethod,
    string Status,
    DateTime? PaidAt,
    string? PaidBy,
    IReadOnlyList<string> Tags,
    string Recurrence,
    DateOnly? NextDueDate,
    DateOnly? RecurrenceEndDate,
    Guid? RecurringSourceId,
    ExpenseReceiptDto? Receipt,
    Guid? ImportBatchId,
    DateTime CreatedAt,
    DateTime UpdatedAt);

/// <summary><c>GET ~/api/v1/admin/billing/expenses?from&amp;to</c>: every live expense dated in [from, to].</summary>
public sealed record OperatingExpenseListDto(
    DateOnly From,
    DateOnly To,
    IReadOnlyList<OperatingExpenseDto> Items,
    ExpenseTotalsDto Totals,
    string? FxNote);

public sealed record ExpenseTotalsDto(int Count, decimal TotalVnd, decimal PaidVnd, decimal PlannedVnd, int Unconverted);

/// <summary>Create or replace an expense. <see cref="Recurrence"/> monthly|yearly makes it a series.</summary>
public sealed record SaveOperatingExpenseRequest(
    DateOnly ExpenseDate,
    string Vendor,
    Guid CategoryId,
    string? Description,
    decimal Amount,
    string Currency,
    string? PaymentMethod,
    string? Status,
    string? PaidBy,
    IReadOnlyList<string>? Tags,
    string? Recurrence,
    DateOnly? RecurrenceEndDate);

public sealed record MarkExpensePaidRequest(DateTime? PaidAt, string? PaidBy, string? PaymentMethod);

public sealed record ExpenseBudgetDto(Guid CategoryId, string Month, decimal AmountVnd, string? Note);

/// <summary>One budget cell; <see cref="AmountVnd"/> null removes the budget of that (category, month).</summary>
public sealed record ExpenseBudgetInput(Guid CategoryId, string Month, decimal? AmountVnd, string? Note);

public sealed record SaveExpenseBudgetsRequest(IReadOnlyList<ExpenseBudgetInput> Items);

// ── Report ────────────────────────────────────────────────────────────────────────────────────

/// <summary><c>GET ~/api/v1/admin/billing/expenses/report?from=yyyy-MM&amp;to=yyyy-MM</c>.</summary>
public sealed record ExpenseReportDto(
    string From,
    string To,
    DateTime GeneratedAt,
    IReadOnlyList<ExpenseCategoryDto> Categories,
    IReadOnlyList<ExpenseReportMonthDto> Months,
    IReadOnlyList<ExpenseCategoryTotalDto> CategoryTotals,
    IReadOnlyList<ExpenseVendorTotalDto> TopVendors,
    IReadOnlyList<ExpenseCommitmentDto> Commitments,
    IReadOnlyList<ExpenseBudgetAlertDto> BudgetAlerts,
    decimal TotalVnd,
    decimal PaidVnd,
    decimal PlannedVnd,
    decimal MonthlyAverageVnd,
    decimal RecurringMonthlyRunRateVnd,
    int Unconverted,
    string? FxNote);

public sealed record ExpenseReportMonthDto(
    string Month,
    decimal TotalVnd,
    decimal PaidVnd,
    decimal PlannedVnd,
    decimal? BudgetVnd,
    IReadOnlyList<ExpenseMonthCategoryDto> Categories);

public sealed record ExpenseMonthCategoryDto(Guid CategoryId, decimal AmountVnd, decimal? BudgetVnd, bool OverBudget);

public sealed record ExpenseCategoryTotalDto(Guid CategoryId, string Slug, string Name, string? Color, decimal AmountVnd, int Count, decimal? BudgetVnd);

public sealed record ExpenseVendorTotalDto(string Vendor, decimal AmountVnd, int Count, string? CategoryName);

/// <summary>A payment expected in the next 90 days: a planned row, or a projected occurrence of a series.</summary>
public sealed record ExpenseCommitmentDto(
    Guid? ExpenseId,
    Guid? SeriesId,
    DateOnly DueDate,
    string Vendor,
    Guid CategoryId,
    string CategoryName,
    decimal Amount,
    string Currency,
    decimal? AmountVnd,
    string Recurrence,
    bool Projected);

public sealed record ExpenseBudgetAlertDto(Guid CategoryId, string CategoryName, string Month, decimal BudgetVnd, decimal ActualVnd, decimal Percent, bool Over);

// ── P&L ───────────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// <c>GET ~/api/v1/admin/billing/expenses/pnl?from=yyyy-MM&amp;to=yyyy-MM&amp;tz</c>: revenue and AI provider
/// cost from the Insights P&amp;L, plus these operating expenses, per local month.
/// </summary>
public sealed record FinancePnlDto(
    string From,
    string To,
    DateTime GeneratedAt,
    IReadOnlyList<FinancePnlMonthDto> Months,
    FinancePnlMonthDto Total,
    IReadOnlyList<ExpenseCategoryDto> Categories,
    string? CostNote,
    string? FxNote,
    string? ExpenseFxNote);

/// <summary>
/// <paramref name="GrossMargin"/> = revenue − AI provider cost; <paramref name="NetResult"/> = gross margin −
/// operating expenses. Null when a term is unknown (no rate, no cost data), never a guessed 0.
/// </summary>
public sealed record FinancePnlMonthDto(
    string Month,
    decimal? Revenue,
    decimal? AiCost,
    decimal? GrossMargin,
    decimal? GrossMarginPercent,
    decimal OperatingExpenses,
    decimal? NetResult,
    decimal? NetMarginPercent,
    IReadOnlyList<ExpenseMonthCategoryDto> ExpensesByCategory,
    decimal CostCoveragePercent);

// ── CSV import ────────────────────────────────────────────────────────────────────────────────

public sealed record ExpenseImportRequest(string Csv, bool SkipInvalid = false);

public sealed record ExpenseImportRowDto(
    int Line,
    bool Valid,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    DateOnly? ExpenseDate,
    string? Vendor,
    Guid? CategoryId,
    string? CategoryName,
    decimal? Amount,
    string? Currency,
    decimal? AmountVnd,
    string? Status,
    string? PaymentMethod,
    string? PaidBy,
    IReadOnlyList<string> Tags,
    string? Description,
    string Recurrence);

public sealed record ExpenseImportPreviewDto(
    IReadOnlyList<string> Columns,
    IReadOnlyList<string> UnknownColumns,
    IReadOnlyList<ExpenseImportRowDto> Rows,
    int ValidCount,
    int InvalidCount,
    decimal TotalVnd);

public sealed record ExpenseImportResultDto(Guid BatchId, int Imported, int Skipped);
