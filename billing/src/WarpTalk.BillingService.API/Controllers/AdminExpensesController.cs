using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Services;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.Shared;
using WarpTalk.Shared.AdminAudit;
using WarpTalk.Shared.Authorization;
using WarpTalk.Shared.Contracts.Admin;
using WarpTalk.Shared.Events;
using WarpTalk.Shared.Extensions;

namespace WarpTalk.BillingService.API.Controllers;

/// <summary>
/// Operating costs and expenses (G12, /admin/finance/expenses): what it costs to run WarpTalk beyond the
/// per-usage provider cost the Insights P&amp;L already knows — servers, domains, SaaS, provider plans,
/// salaries, marketing. Under ~/api/v1/admin/billing, which the gateway's admin-billing route already
/// forwards here.
///
/// Reads need finance.expenses_read, every write finance.expenses_manage — not billing.read: salaries sit
/// in here, and the Support role reads billing.
/// </summary>
[ApiController]
[Route("api/v1/admin/billing/expenses")]
public sealed class AdminExpensesController : ControllerBase
{
    private readonly IOperatingExpenseService _expenses;
    private readonly IAdminInboxSourceService _inbox;

    public AdminExpensesController(IOperatingExpenseService expenses, IAdminInboxSourceService inbox)
    {
        _expenses = expenses;
        _inbox = inbox;
    }

    // ── Expenses ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Every expense dated in [from, to] (default: the last 12 months), with VND totals.</summary>
    [HttpGet]
    [RequirePermission(AdminPermissions.FinanceExpensesRead)]
    public async Task<IActionResult> GetExpenses([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct)
        => ToActionResult(await _expenses.GetExpensesAsync(from, to, ct));

    [HttpGet("{id:guid}")]
    [RequirePermission(AdminPermissions.FinanceExpensesRead)]
    public async Task<IActionResult> GetExpense(Guid id, CancellationToken ct)
        => ToActionResult(await _expenses.GetExpenseAsync(id, ct));

    [HttpPost]
    [RequirePermission(AdminPermissions.FinanceExpensesManage)]
    [AdminAudited(AdminAuditExpenseActions.Created, AdminAuditEntityTypes.OperatingExpense, typeof(OperatingExpense))]
    public async Task<IActionResult> CreateExpense([FromBody] SaveOperatingExpenseRequest request, CancellationToken ct)
        => await WithActorAsync(actor => _expenses.CreateExpenseAsync(request, actor, ct));

    [HttpPut("{id:guid}")]
    [RequirePermission(AdminPermissions.FinanceExpensesManage)]
    [AdminAudited(AdminAuditExpenseActions.Updated, AdminAuditEntityTypes.OperatingExpense, typeof(OperatingExpense), EntityRouteKey = "id")]
    public async Task<IActionResult> UpdateExpense(Guid id, [FromBody] SaveOperatingExpenseRequest request, CancellationToken ct)
        => await WithActorAsync(actor => _expenses.UpdateExpenseAsync(id, request, actor, ct));

    [HttpPost("{id:guid}/mark-paid")]
    [RequirePermission(AdminPermissions.FinanceExpensesManage)]
    [AdminAudited(AdminAuditExpenseActions.MarkedPaid, AdminAuditEntityTypes.OperatingExpense, typeof(OperatingExpense), EntityRouteKey = "id")]
    public async Task<IActionResult> MarkPaid(Guid id, [FromBody] MarkExpensePaidRequest? request, CancellationToken ct)
        => await WithActorAsync(actor => _expenses.MarkPaidAsync(id, request ?? new MarkExpensePaidRequest(null, null, null), actor, ct));

    [HttpDelete("{id:guid}")]
    [RequirePermission(AdminPermissions.FinanceExpensesManage)]
    [AdminAudited(AdminAuditExpenseActions.Deleted, AdminAuditEntityTypes.OperatingExpense, typeof(OperatingExpense), EntityRouteKey = "id")]
    public async Task<IActionResult> DeleteExpense(Guid id, CancellationToken ct)
    {
        var actor = User.GetUserId();
        if (actor is null) return Unauthorized(new ApiErrorResponse("Invalid or missing user identity.", ErrorCodes.Unauthorized));
        var result = await _expenses.DeleteExpenseAsync(id, actor.Value, ct);
        return result.IsSuccess ? NoContent() : ToActionResult(result);
    }

    // ── Receipts ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Multipart field <c>file</c>: PDF, PNG, JPEG, WebP or HEIC, at most 10 MB. Replaces an earlier receipt.</summary>
    [HttpPost("{id:guid}/receipt")]
    [RequestSizeLimit(OperatingExpenseConstants.MaxReceiptBytes + 64 * 1024)]
    [RequirePermission(AdminPermissions.FinanceExpensesManage)]
    [AdminAudited(AdminAuditExpenseActions.ReceiptAttached, AdminAuditEntityTypes.OperatingExpense, typeof(OperatingExpense), EntityRouteKey = "id")]
    public async Task<IActionResult> AttachReceipt(Guid id, IFormFile? file, CancellationToken ct)
    {
        if (file is null) return BadRequest(new ApiErrorResponse("Attach the receipt as the multipart field 'file'.", ErrorCodes.ValidationError));
        await using var stream = file.OpenReadStream();
        return await WithActorAsync(actor => _expenses.AttachReceiptAsync(id, file.FileName, file.ContentType, file.Length, stream, actor, ct));
    }

    [HttpGet("{id:guid}/receipt")]
    [RequirePermission(AdminPermissions.FinanceExpensesRead)]
    public async Task<IActionResult> DownloadReceipt(Guid id, CancellationToken ct)
    {
        var result = await _expenses.OpenReceiptAsync(id, ct);
        if (!result.IsSuccess) return ToActionResult(result);
        var (content, fileName, contentType) = result.Value;
        return File(content, contentType, fileName);
    }

    [HttpDelete("{id:guid}/receipt")]
    [RequirePermission(AdminPermissions.FinanceExpensesManage)]
    [AdminAudited(AdminAuditExpenseActions.ReceiptRemoved, AdminAuditEntityTypes.OperatingExpense, typeof(OperatingExpense), EntityRouteKey = "id")]
    public async Task<IActionResult> RemoveReceipt(Guid id, CancellationToken ct)
        => await WithActorAsync(actor => _expenses.RemoveReceiptAsync(id, actor, ct));

    // ── Categories ────────────────────────────────────────────────────────────────────────────

    [HttpGet("categories")]
    [RequirePermission(AdminPermissions.FinanceExpensesRead)]
    public async Task<IActionResult> GetCategories(CancellationToken ct)
        => ToActionResult(await _expenses.GetCategoriesAsync(ct));

    [HttpPost("categories")]
    [RequirePermission(AdminPermissions.FinanceExpensesManage)]
    [AdminAudited(AdminAuditExpenseActions.CategoryCreated, AdminAuditEntityTypes.ExpenseCategory, typeof(ExpenseCategory))]
    public async Task<IActionResult> CreateCategory([FromBody] SaveExpenseCategoryRequest request, CancellationToken ct)
        => await WithActorAsync(actor => _expenses.CreateCategoryAsync(request, actor, ct));

    [HttpPut("categories/{id:guid}")]
    [RequirePermission(AdminPermissions.FinanceExpensesManage)]
    [AdminAudited(AdminAuditExpenseActions.CategoryUpdated, AdminAuditEntityTypes.ExpenseCategory, typeof(ExpenseCategory), EntityRouteKey = "id")]
    public async Task<IActionResult> UpdateCategory(Guid id, [FromBody] SaveExpenseCategoryRequest request, CancellationToken ct)
        => await WithActorAsync(actor => _expenses.UpdateCategoryAsync(id, request, actor, ct));

    // ── Budgets ───────────────────────────────────────────────────────────────────────────────

    /// <summary><c>?from=yyyy-MM&amp;to=yyyy-MM</c> (default: the last 12 months).</summary>
    [HttpGet("budgets")]
    [RequirePermission(AdminPermissions.FinanceExpensesRead)]
    public async Task<IActionResult> GetBudgets([FromQuery] string? from, [FromQuery] string? to, CancellationToken ct)
        => ToActionResult(await _expenses.GetBudgetsAsync(from, to, ct));

    /// <summary>Sets or clears (amountVnd null) budget cells.</summary>
    [HttpPut("budgets")]
    [RequirePermission(AdminPermissions.FinanceExpensesManage)]
    [AdminAudited(AdminAuditExpenseActions.BudgetsSet, AdminAuditEntityTypes.ExpenseBudget, typeof(ExpenseBudget), Aggregate = true)]
    public async Task<IActionResult> SaveBudgets([FromBody] SaveExpenseBudgetsRequest request, CancellationToken ct)
        => await WithActorAsync(actor => _expenses.SaveBudgetsAsync(request, actor, ct));

    // ── Reports ───────────────────────────────────────────────────────────────────────────────

    /// <summary><c>?from=yyyy-MM&amp;to=yyyy-MM</c>, at most 24 months (default: the last 12).</summary>
    [HttpGet("report")]
    [RequirePermission(AdminPermissions.FinanceExpensesRead)]
    public async Task<IActionResult> GetReport([FromQuery] string? from, [FromQuery] string? to, CancellationToken ct)
        => ToActionResult(await _expenses.GetReportAsync(from, to, ct));

    /// <summary>
    /// Revenue and AI provider cost (the Insights P&amp;L) with these operating expenses, per local month:
    /// <c>?from=yyyy-MM&amp;to=yyyy-MM&amp;tz</c>, at most 12 months (default: the last 6).
    /// </summary>
    [HttpGet("pnl")]
    [RequirePermission(AdminPermissions.FinanceExpensesRead)]
    public async Task<IActionResult> GetProfitAndLoss([FromQuery] string? from, [FromQuery] string? to, [FromQuery] string? tz, CancellationToken ct)
        => ToActionResult(await _expenses.GetProfitAndLossAsync(from, to, tz, ct));

    // ── CSV import ────────────────────────────────────────────────────────────────────────────

    /// <summary>Validates a CSV and shows what would be imported. Writes nothing.</summary>
    [HttpPost("import/preview")]
    [RequirePermission(AdminPermissions.FinanceExpensesManage)]
    public async Task<IActionResult> PreviewImport([FromBody] ExpenseImportRequest request, CancellationToken ct)
        => ToActionResult(await _expenses.PreviewImportAsync(request, ct));

    /// <summary>Imports the CSV in one transaction. With errors, refuses unless <c>skipInvalid</c>.</summary>
    [HttpPost("import")]
    [RequirePermission(AdminPermissions.FinanceExpensesManage)]
    [AdminAudited(AdminAuditExpenseActions.Imported, AdminAuditEntityTypes.OperatingExpense, typeof(OperatingExpense), Aggregate = true)]
    public async Task<IActionResult> Import([FromBody] ExpenseImportRequest request, CancellationToken ct)
        => await WithActorAsync(actor => _expenses.ImportAsync(request, actor, ct));

    // ── Inbox ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>The pending-work inbox source: planned expenses due within 7 days or overdue.</summary>
    [HttpGet("inbox-items")]
    [RequirePermission(AdminPermissions.FinanceExpensesRead)]
    public async Task<ActionResult<AdminInboxSourceResponse>> GetInboxItems(CancellationToken ct)
        => Ok(await _inbox.GetExpenseItemsAsync(ct));

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────

    private async Task<IActionResult> WithActorAsync<T>(Func<Guid, Task<Result<T>>> action)
    {
        var actor = User.GetUserId();
        if (actor is null) return Unauthorized(new ApiErrorResponse("Invalid or missing user identity.", ErrorCodes.Unauthorized));
        return ToActionResult(await action(actor.Value));
    }

    private IActionResult ToActionResult<T>(Result<T> result)
    {
        if (result.IsSuccess) return Ok(result.Value);

        var error = new ApiErrorResponse(result.Error, result.ErrorCode);
        return result.ErrorCode switch
        {
            ErrorCodes.NotFound => NotFound(error),
            ErrorCodes.ValidationError => BadRequest(error),
            ErrorCodes.Conflict => Conflict(error),
            ErrorCodes.ServiceUnavailable => StatusCode(StatusCodes.Status503ServiceUnavailable, error),
            _ => StatusCode(StatusCodes.Status500InternalServerError, error),
        };
    }
}
