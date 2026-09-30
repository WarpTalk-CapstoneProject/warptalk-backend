using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Interfaces;

/// <summary>G12 operating expenses: categories, expenses, receipts, budgets, reports, P&amp;L and CSV import.</summary>
public interface IOperatingExpenseService
{
    Task<Result<IReadOnlyList<ExpenseCategoryDto>>> GetCategoriesAsync(CancellationToken ct = default);
    Task<Result<ExpenseCategoryDto>> CreateCategoryAsync(SaveExpenseCategoryRequest request, Guid actorId, CancellationToken ct = default);
    Task<Result<ExpenseCategoryDto>> UpdateCategoryAsync(Guid id, SaveExpenseCategoryRequest request, Guid actorId, CancellationToken ct = default);

    Task<Result<OperatingExpenseListDto>> GetExpensesAsync(DateOnly? from, DateOnly? to, CancellationToken ct = default);
    Task<Result<OperatingExpenseDto>> GetExpenseAsync(Guid id, CancellationToken ct = default);
    Task<Result<OperatingExpenseDto>> CreateExpenseAsync(SaveOperatingExpenseRequest request, Guid actorId, CancellationToken ct = default);
    Task<Result<OperatingExpenseDto>> UpdateExpenseAsync(Guid id, SaveOperatingExpenseRequest request, Guid actorId, CancellationToken ct = default);
    Task<Result<OperatingExpenseDto>> MarkPaidAsync(Guid id, MarkExpensePaidRequest request, Guid actorId, CancellationToken ct = default);
    Task<Result<bool>> DeleteExpenseAsync(Guid id, Guid actorId, CancellationToken ct = default);

    Task<Result<OperatingExpenseDto>> AttachReceiptAsync(Guid id, string fileName, string? contentType, long length, Stream content, Guid actorId, CancellationToken ct = default);
    Task<Result<(Stream Content, string FileName, string ContentType)>> OpenReceiptAsync(Guid id, CancellationToken ct = default);
    Task<Result<OperatingExpenseDto>> RemoveReceiptAsync(Guid id, Guid actorId, CancellationToken ct = default);

    Task<Result<IReadOnlyList<ExpenseBudgetDto>>> GetBudgetsAsync(string? from, string? to, CancellationToken ct = default);
    Task<Result<IReadOnlyList<ExpenseBudgetDto>>> SaveBudgetsAsync(SaveExpenseBudgetsRequest request, Guid actorId, CancellationToken ct = default);

    Task<Result<ExpenseReportDto>> GetReportAsync(string? from, string? to, CancellationToken ct = default);
    Task<Result<FinancePnlDto>> GetProfitAndLossAsync(string? from, string? to, string? tz, CancellationToken ct = default);

    Task<Result<ExpenseImportPreviewDto>> PreviewImportAsync(ExpenseImportRequest request, CancellationToken ct = default);
    Task<Result<ExpenseImportResultDto>> ImportAsync(ExpenseImportRequest request, Guid actorId, CancellationToken ct = default);

    /// <summary>Writes the occurrences of every series due within the lead time. Returns how many were written.</summary>
    Task<int> GenerateDueOccurrencesAsync(DateOnly today, CancellationToken ct = default);
}
