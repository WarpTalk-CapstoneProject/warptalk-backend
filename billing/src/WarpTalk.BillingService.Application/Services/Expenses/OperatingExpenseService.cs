using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.Shared;
using WarpTalk.Shared.Contracts.Admin;

namespace WarpTalk.BillingService.Application.Services.Expenses;

/// <summary>
/// G12 operating expenses. Everything VND is converted on read at the USD→VND rate of the expense's
/// own date (<see cref="FxRateTable"/>, the table the Insights P&amp;L uses), so an FX correction reaches
/// every past figure and there is no stored VND amount to go stale.
/// </summary>
public sealed partial class OperatingExpenseService : IOperatingExpenseService
{
    private const int DefaultListMonths = 12;
    private const int MaxListMonths = 60;

    private readonly IUnitOfWork _unitOfWork;
    private readonly IFxRateService _fx;
    private readonly IExpenseReceiptStorage _receipts;
    private readonly IAdminBillingInsightsService _insights;
    private readonly TimeProvider _time;
    private readonly ILogger<OperatingExpenseService> _logger;

    public OperatingExpenseService(
        IUnitOfWork unitOfWork,
        IFxRateService fx,
        IExpenseReceiptStorage receipts,
        IAdminBillingInsightsService insights,
        ILogger<OperatingExpenseService> logger,
        TimeProvider? time = null)
    {
        _unitOfWork = unitOfWork;
        _fx = fx;
        _receipts = receipts;
        _insights = insights;
        _time = time ?? TimeProvider.System;
        _logger = logger;
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;
    private DateOnly Today => DateOnly.FromDateTime(Now);

    // ── Categories ────────────────────────────────────────────────────────────────────────────

    public async Task<Result<IReadOnlyList<ExpenseCategoryDto>>> GetCategoriesAsync(CancellationToken ct = default)
        => Result.Success(await CategoryDtosAsync(ct));

    private async Task<IReadOnlyList<ExpenseCategoryDto>> CategoryDtosAsync(CancellationToken ct)
    {
        var categories = await _unitOfWork.ExpenseCategories.ListOrderedAsync(ct);
        var result = new List<ExpenseCategoryDto>(categories.Count);
        foreach (var category in categories)
        {
            result.Add(ToDto(category, await _unitOfWork.ExpenseCategories.IsInUseAsync(category.Id, ct)));
        }

        return result;
    }

    public async Task<Result<ExpenseCategoryDto>> CreateCategoryAsync(SaveExpenseCategoryRequest request, Guid actorId, CancellationToken ct = default)
    {
        var error = ValidateCategory(request);
        if (error is not null) return Result.Failure<ExpenseCategoryDto>(error, ErrorCodes.ValidationError);

        var slug = string.IsNullOrWhiteSpace(request.Slug) ? Slugify(request.Name) : request.Slug.Trim().ToLowerInvariant();
        if (!SlugPattern().IsMatch(slug))
            return Result.Failure<ExpenseCategoryDto>("The slug may contain lowercase letters, digits and underscores only.", ErrorCodes.ValidationError);
        if (await _unitOfWork.ExpenseCategories.GetBySlugAsync(slug, ct) is not null)
            return Result.Failure<ExpenseCategoryDto>($"A category with the slug '{slug}' already exists.", ErrorCodes.Conflict);

        var now = Now;
        var category = new ExpenseCategory
        {
            Id = Guid.NewGuid(),
            Slug = slug,
            Name = request.Name.Trim(),
            Description = Blank(request.Description),
            Color = Blank(request.Color),
            SortOrder = request.SortOrder ?? 100,
            IsActive = request.IsActive ?? true,
            CreatedAt = now,
            UpdatedAt = now,
            UpdatedBy = actorId,
        };
        await _unitOfWork.ExpenseCategories.AddAsync(category, ct);
        await _unitOfWork.SaveChangesAsync(ct);
        return Result.Success(ToDto(category, false));
    }

    public async Task<Result<ExpenseCategoryDto>> UpdateCategoryAsync(Guid id, SaveExpenseCategoryRequest request, Guid actorId, CancellationToken ct = default)
    {
        var error = ValidateCategory(request);
        if (error is not null) return Result.Failure<ExpenseCategoryDto>(error, ErrorCodes.ValidationError);

        var category = await _unitOfWork.ExpenseCategories.GetByIdAsync(id, ct);
        if (category is null) return Result.Failure<ExpenseCategoryDto>("Expense category not found.", ErrorCodes.NotFound);

        // The slug is the stable key CSV imports refer to: set once, never renamed.
        category.Name = request.Name.Trim();
        category.Description = Blank(request.Description);
        category.Color = Blank(request.Color);
        if (request.SortOrder is { } order) category.SortOrder = order;
        if (request.IsActive is { } active) category.IsActive = active;
        category.UpdatedAt = Now;
        category.UpdatedBy = actorId;
        await _unitOfWork.SaveChangesAsync(ct);
        return Result.Success(ToDto(category, await _unitOfWork.ExpenseCategories.IsInUseAsync(id, ct)));
    }

    private static string? ValidateCategory(SaveExpenseCategoryRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) return "The category needs a name.";
        if (request.Name.Trim().Length > 120) return "The category name may be at most 120 characters.";
        if (request.Description?.Length > 500) return "The description may be at most 500 characters.";
        if (request.Color?.Length > 20) return "The color may be at most 20 characters.";
        return null;
    }

    private static string Slugify(string name)
    {
        var slug = NonSlugChars().Replace(name.Trim().ToLowerInvariant(), "_").Trim('_');
        if (slug.Length == 0 || !char.IsAsciiLetterOrDigit(slug[0])) slug = "category_" + slug;
        return slug.Length > 60 ? slug[..60].TrimEnd('_') : slug;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9_]*$")]
    private static partial Regex SlugPattern();

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlugChars();

    // ── Expenses ──────────────────────────────────────────────────────────────────────────────

    public async Task<Result<OperatingExpenseListDto>> GetExpensesAsync(DateOnly? from, DateOnly? to, CancellationToken ct = default)
    {
        var end = to ?? ExpenseRecurrence.LastDayOfMonth(ExpenseRecurrence.FirstOfMonth(Today));
        var start = from ?? ExpenseRecurrence.FirstOfMonth(end).AddMonths(-(DefaultListMonths - 1));
        if (start > end) return Result.Failure<OperatingExpenseListDto>("from must not be after to.", ErrorCodes.ValidationError);
        if (start < end.AddMonths(-MaxListMonths))
            return Result.Failure<OperatingExpenseListDto>($"The range may cover at most {MaxListMonths} months.", ErrorCodes.ValidationError);

        var rows = await _unitOfWork.OperatingExpenses.ListInRangeAsync(start, end, ct);
        var fx = await _fx.GetTableAsync(ct);
        var converted = rows.Select(row => Convert(row, fx)).ToList();
        var items = converted.Select(c => ToDto(c)).OrderByDescending(item => item.ExpenseDate).ThenByDescending(item => item.CreatedAt).ToList();
        return Result.Success(new OperatingExpenseListDto(start, end, items, Totals(converted), Describe(converted)));
    }

    public async Task<Result<OperatingExpenseDto>> GetExpenseAsync(Guid id, CancellationToken ct = default)
    {
        var expense = await _unitOfWork.OperatingExpenses.GetLiveAsync(id, ct);
        if (expense is null) return NotFound();
        return Result.Success(ToDto(Convert(expense, await _fx.GetTableAsync(ct))));
    }

    public async Task<Result<OperatingExpenseDto>> CreateExpenseAsync(SaveOperatingExpenseRequest request, Guid actorId, CancellationToken ct = default)
    {
        var category = await _unitOfWork.ExpenseCategories.GetByIdAsync(request.CategoryId, ct);
        var validation = Validate(request, category, currentCategoryId: null);
        if (validation.Error is not null) return Result.Failure<OperatingExpenseDto>(validation.Error, ErrorCodes.ValidationError);

        var now = Now;
        var expense = new OperatingExpense { Id = Guid.NewGuid(), CreatedAt = now, CreatedBy = actorId };
        Apply(expense, request, validation, now, actorId, isNew: true);
        await _unitOfWork.OperatingExpenses.AddAsync(expense, ct);
        await _unitOfWork.SaveChangesAsync(ct);
        expense.Category = category;
        return Result.Success(ToDto(Convert(expense, await _fx.GetTableAsync(ct))));
    }

    public async Task<Result<OperatingExpenseDto>> UpdateExpenseAsync(Guid id, SaveOperatingExpenseRequest request, Guid actorId, CancellationToken ct = default)
    {
        var expense = await _unitOfWork.OperatingExpenses.GetLiveAsync(id, ct);
        if (expense is null) return NotFound();

        var category = expense.CategoryId == request.CategoryId
            ? expense.Category
            : await _unitOfWork.ExpenseCategories.GetByIdAsync(request.CategoryId, ct);
        var validation = Validate(request, category, expense.CategoryId);
        if (validation.Error is not null) return Result.Failure<OperatingExpenseDto>(validation.Error, ErrorCodes.ValidationError);
        if (expense.RecurringSourceId is not null && validation.Recurrence != OperatingExpenseConstants.Recurrences.None)
        {
            return Result.Failure<OperatingExpenseDto>(
                "This is one occurrence of a recurring expense. Change the repeat on the series instead.", ErrorCodes.ValidationError);
        }

        Apply(expense, request, validation, Now, actorId, isNew: false);
        await _unitOfWork.SaveChangesAsync(ct);
        expense.Category = category;
        return Result.Success(ToDto(Convert(expense, await _fx.GetTableAsync(ct))));
    }

    public async Task<Result<OperatingExpenseDto>> MarkPaidAsync(Guid id, MarkExpensePaidRequest request, Guid actorId, CancellationToken ct = default)
    {
        var expense = await _unitOfWork.OperatingExpenses.GetLiveAsync(id, ct);
        if (expense is null) return NotFound();
        if (request.PaidBy?.Length > 200) return Result.Failure<OperatingExpenseDto>("Paid by may be at most 200 characters.", ErrorCodes.ValidationError);
        if (request.PaymentMethod?.Length > 40) return Result.Failure<OperatingExpenseDto>("The payment method may be at most 40 characters.", ErrorCodes.ValidationError);

        expense.Status = OperatingExpenseConstants.Statuses.Paid;
        expense.PaidAt = request.PaidAt is { } paidAt ? DateTime.SpecifyKind(paidAt.ToUniversalTime(), DateTimeKind.Utc) : Now;
        if (!string.IsNullOrWhiteSpace(request.PaidBy)) expense.PaidBy = request.PaidBy.Trim();
        if (!string.IsNullOrWhiteSpace(request.PaymentMethod)) expense.PaymentMethod = request.PaymentMethod.Trim();
        expense.UpdatedAt = Now;
        expense.UpdatedBy = actorId;
        await _unitOfWork.SaveChangesAsync(ct);
        return Result.Success(ToDto(Convert(expense, await _fx.GetTableAsync(ct))));
    }

    public async Task<Result<bool>> DeleteExpenseAsync(Guid id, Guid actorId, CancellationToken ct = default)
    {
        var expense = await _unitOfWork.OperatingExpenses.GetLiveAsync(id, ct);
        if (expense is null) return Result.Failure<bool>("Expense not found.", ErrorCodes.NotFound);

        // Soft delete. Deleting a series stops it; the occurrences already written stay, because each
        // one is a bill of its own.
        expense.DeletedAt = Now;
        expense.UpdatedAt = Now;
        expense.UpdatedBy = actorId;
        await _unitOfWork.SaveChangesAsync(ct);
        return Result.Success(true);
    }

    private sealed record Validation(string? Error, string Currency, string Status, string Recurrence, List<string> Tags);

    private static Validation Validate(SaveOperatingExpenseRequest request, ExpenseCategory? category, Guid? currentCategoryId)
    {
        static Validation Fail(string error) => new(error, string.Empty, string.Empty, string.Empty, []);

        if (category is null) return Fail("Choose a category.");
        if (!category.IsActive && category.Id != currentCategoryId) return Fail($"The category '{category.Name}' is retired.");
        if (string.IsNullOrWhiteSpace(request.Vendor)) return Fail("The vendor is required.");
        if (request.Vendor.Trim().Length > 200) return Fail("The vendor may be at most 200 characters.");
        if (request.Description?.Length > 2000) return Fail("The description may be at most 2000 characters.");
        if (request.Amount < 0) return Fail("The amount must not be negative.");
        if (request.Amount > 1_000_000_000_000m) return Fail("The amount is too large.");
        if (request.ExpenseDate.Year < 2000 || request.ExpenseDate.Year > 2100) return Fail("The date is out of range.");

        var currency = (request.Currency ?? string.Empty).Trim().ToUpperInvariant();
        if (!OperatingExpenseConstants.Currencies.All.Contains(currency)) return Fail("The currency must be VND or USD.");
        if (currency == OperatingExpenseConstants.Currencies.Vnd && decimal.Truncate(request.Amount) != request.Amount)
            return Fail("A VND amount has no decimals.");

        var status = string.IsNullOrWhiteSpace(request.Status) ? OperatingExpenseConstants.Statuses.Paid : request.Status.Trim().ToLowerInvariant();
        if (!OperatingExpenseConstants.Statuses.All.Contains(status)) return Fail("The status must be planned or paid.");

        var recurrence = string.IsNullOrWhiteSpace(request.Recurrence) ? OperatingExpenseConstants.Recurrences.None : request.Recurrence.Trim().ToLowerInvariant();
        if (!OperatingExpenseConstants.Recurrences.All.Contains(recurrence)) return Fail("The repeat must be none, monthly or yearly.");
        if (request.RecurrenceEndDate is { } end && end < request.ExpenseDate) return Fail("The repeat cannot end before the first date.");

        if (request.PaymentMethod?.Trim().Length > 40) return Fail("The payment method may be at most 40 characters.");
        if (request.PaidBy?.Trim().Length > 200) return Fail("Paid by may be at most 200 characters.");

        var tags = (request.Tags ?? [])
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (tags.Count > OperatingExpenseConstants.MaxTags) return Fail($"At most {OperatingExpenseConstants.MaxTags} tags.");
        if (tags.Any(tag => tag.Length > OperatingExpenseConstants.MaxTagLength))
            return Fail($"A tag may be at most {OperatingExpenseConstants.MaxTagLength} characters.");

        return new Validation(null, currency, status, recurrence, tags);
    }

    private void Apply(OperatingExpense expense, SaveOperatingExpenseRequest request, Validation validation, DateTime now, Guid actorId, bool isNew)
    {
        var scheduleChanged = isNew
            || expense.Recurrence != validation.Recurrence
            || expense.ExpenseDate != request.ExpenseDate;

        expense.ExpenseDate = request.ExpenseDate;
        expense.Vendor = request.Vendor.Trim();
        expense.CategoryId = request.CategoryId;
        expense.Description = Blank(request.Description);
        expense.Amount = request.Amount;
        expense.Currency = validation.Currency;
        expense.PaymentMethod = Blank(request.PaymentMethod);
        expense.PaidBy = Blank(request.PaidBy);
        expense.Tags = validation.Tags;
        expense.RecurrenceEndDate = validation.Recurrence == OperatingExpenseConstants.Recurrences.None ? null : request.RecurrenceEndDate;

        if (validation.Status == OperatingExpenseConstants.Statuses.Paid)
        {
            if (expense.Status != OperatingExpenseConstants.Statuses.Paid || expense.PaidAt is null)
            {
                // Recorded as already paid: paid on its own date unless that date is still ahead.
                var dated = DateTime.SpecifyKind(request.ExpenseDate.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
                expense.PaidAt = dated <= now ? dated : now;
            }
        }
        else
        {
            expense.PaidAt = null;
        }

        expense.Status = validation.Status;

        if (scheduleChanged)
        {
            expense.Recurrence = validation.Recurrence;
            // A series recorded with a past first date starts from today; it does not back-fill.
            expense.NextDueDate = ExpenseRecurrence.NextAfter(request.ExpenseDate, validation.Recurrence, request.ExpenseDate, Today);
        }

        expense.UpdatedAt = now;
        expense.UpdatedBy = actorId;
    }

    private static Result<OperatingExpenseDto> NotFound() => Result.Failure<OperatingExpenseDto>("Expense not found.", ErrorCodes.NotFound);

    // ── Receipts ──────────────────────────────────────────────────────────────────────────────

    public async Task<Result<OperatingExpenseDto>> AttachReceiptAsync(
        Guid id, string fileName, string? contentType, long length, Stream content, Guid actorId, CancellationToken ct = default)
    {
        if (!_receipts.IsAvailable)
            return Result.Failure<OperatingExpenseDto>("Receipt storage is not configured on this server.", ErrorCodes.ServiceUnavailable);
        if (length <= 0) return Result.Failure<OperatingExpenseDto>("The file is empty.", ErrorCodes.ValidationError);
        if (length > OperatingExpenseConstants.MaxReceiptBytes)
            return Result.Failure<OperatingExpenseDto>("A receipt may be at most 10 MB.", ErrorCodes.ValidationError);

        var extension = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
        if (!OperatingExpenseConstants.ReceiptContentTypes.TryGetValue(extension, out var knownType))
            return Result.Failure<OperatingExpenseDto>("A receipt must be a PDF, PNG, JPEG, WebP or HEIC file.", ErrorCodes.ValidationError);

        var expense = await _unitOfWork.OperatingExpenses.GetLiveAsync(id, ct);
        if (expense is null) return NotFound();

        var previousKey = expense.ReceiptStorageKey;
        var key = $"finance/receipts/{id:N}/{Guid.NewGuid():N}{extension}";
        await _receipts.SaveAsync(key, content, knownType, ct);

        expense.ReceiptStorageKey = key;
        expense.ReceiptFileName = Path.GetFileName(fileName!).Trim() is { Length: > 0 } name ? Truncate(name, 255) : "receipt" + extension;
        expense.ReceiptContentType = knownType;
        expense.ReceiptSizeBytes = length;
        expense.UpdatedAt = Now;
        expense.UpdatedBy = actorId;
        try
        {
            await _unitOfWork.SaveChangesAsync(ct);
        }
        catch
        {
            await DeleteQuietlyAsync(key);
            throw;
        }

        if (previousKey is not null) await DeleteQuietlyAsync(previousKey);
        return Result.Success(ToDto(Convert(expense, await _fx.GetTableAsync(ct))));
    }

    public async Task<Result<(Stream Content, string FileName, string ContentType)>> OpenReceiptAsync(Guid id, CancellationToken ct = default)
    {
        var expense = await _unitOfWork.OperatingExpenses.GetLiveAsync(id, ct);
        if (expense?.ReceiptStorageKey is null)
            return Result.Failure<(Stream, string, string)>("This expense has no receipt.", ErrorCodes.NotFound);
        if (!_receipts.IsAvailable)
            return Result.Failure<(Stream, string, string)>("Receipt storage is not configured on this server.", ErrorCodes.ServiceUnavailable);

        var stream = await _receipts.OpenReadAsync(expense.ReceiptStorageKey, ct);
        if (stream is null) return Result.Failure<(Stream, string, string)>("The receipt file is missing from storage.", ErrorCodes.NotFound);
        return Result.Success((stream, expense.ReceiptFileName ?? "receipt", expense.ReceiptContentType ?? "application/octet-stream"));
    }

    public async Task<Result<OperatingExpenseDto>> RemoveReceiptAsync(Guid id, Guid actorId, CancellationToken ct = default)
    {
        var expense = await _unitOfWork.OperatingExpenses.GetLiveAsync(id, ct);
        if (expense is null) return NotFound();
        var key = expense.ReceiptStorageKey;
        if (key is null) return Result.Success(ToDto(Convert(expense, await _fx.GetTableAsync(ct))));

        expense.ReceiptStorageKey = null;
        expense.ReceiptFileName = null;
        expense.ReceiptContentType = null;
        expense.ReceiptSizeBytes = null;
        expense.UpdatedAt = Now;
        expense.UpdatedBy = actorId;
        await _unitOfWork.SaveChangesAsync(ct);
        if (_receipts.IsAvailable) await DeleteQuietlyAsync(key);
        return Result.Success(ToDto(Convert(expense, await _fx.GetTableAsync(ct))));
    }

    private async Task DeleteQuietlyAsync(string key)
    {
        try
        {
            await _receipts.DeleteAsync(key, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not delete receipt object {StorageKey}; it is orphaned but unreferenced.", key);
        }
    }

    // ── Budgets ───────────────────────────────────────────────────────────────────────────────

    public async Task<Result<IReadOnlyList<ExpenseBudgetDto>>> GetBudgetsAsync(string? from, string? to, CancellationToken ct = default)
    {
        if (!ExpenseRecurrence.TryResolveMonths(from, to, Today, DefaultListMonths, OperatingExpenseConstants.MaxReportMonths,
                out var fromMonth, out var toMonth, out var error))
        {
            return Result.Failure<IReadOnlyList<ExpenseBudgetDto>>(error!, ErrorCodes.ValidationError);
        }

        var budgets = await _unitOfWork.ExpenseBudgets.ListInRangeAsync(fromMonth, toMonth, ct);
        return Result.Success<IReadOnlyList<ExpenseBudgetDto>>(budgets
            .OrderBy(budget => budget.Month)
            .Select(ToDto)
            .ToList());
    }

    public async Task<Result<IReadOnlyList<ExpenseBudgetDto>>> SaveBudgetsAsync(SaveExpenseBudgetsRequest request, Guid actorId, CancellationToken ct = default)
    {
        var items = request.Items ?? [];
        if (items.Count == 0) return Result.Failure<IReadOnlyList<ExpenseBudgetDto>>("Nothing to save.", ErrorCodes.ValidationError);
        if (items.Count > 500) return Result.Failure<IReadOnlyList<ExpenseBudgetDto>>("At most 500 budget cells at a time.", ErrorCodes.ValidationError);

        var categories = (await _unitOfWork.ExpenseCategories.ListOrderedAsync(ct)).Select(c => c.Id).ToHashSet();
        var parsed = new List<(ExpenseBudgetInput Input, DateOnly Month)>();
        foreach (var item in items)
        {
            if (!categories.Contains(item.CategoryId))
                return Result.Failure<IReadOnlyList<ExpenseBudgetDto>>("Unknown expense category.", ErrorCodes.ValidationError);
            if (!ExpenseRecurrence.TryParseMonth(item.Month, out var month))
                return Result.Failure<IReadOnlyList<ExpenseBudgetDto>>("Each budget month must be yyyy-MM.", ErrorCodes.ValidationError);
            if (item.AmountVnd is < 0 or > 1_000_000_000_000m)
                return Result.Failure<IReadOnlyList<ExpenseBudgetDto>>("A budget must be between 0 and 1,000,000,000,000 VND.", ErrorCodes.ValidationError);
            if (item.Note?.Length > 500)
                return Result.Failure<IReadOnlyList<ExpenseBudgetDto>>("A budget note may be at most 500 characters.", ErrorCodes.ValidationError);
            parsed.Add((item, month));
        }

        var now = Now;
        var saved = new List<ExpenseBudget>();
        foreach (var (input, month) in parsed.GroupBy(p => (p.Input.CategoryId, p.Month)).Select(g => g.Last()))
        {
            var existing = await _unitOfWork.ExpenseBudgets.GetAsync(input.CategoryId, month, ct);
            if (input.AmountVnd is null)
            {
                if (existing is not null) _unitOfWork.ExpenseBudgets.Remove(existing);
                continue;
            }

            if (existing is null)
            {
                existing = new ExpenseBudget
                {
                    Id = Guid.NewGuid(),
                    CategoryId = input.CategoryId,
                    Month = month,
                    CreatedAt = now,
                };
                await _unitOfWork.ExpenseBudgets.AddAsync(existing, ct);
            }

            existing.AmountVnd = decimal.Round(input.AmountVnd.Value, 0, MidpointRounding.AwayFromZero);
            existing.Note = Blank(input.Note);
            existing.UpdatedAt = now;
            existing.UpdatedBy = actorId;
            saved.Add(existing);
        }

        await _unitOfWork.SaveChangesAsync(ct);
        return Result.Success<IReadOnlyList<ExpenseBudgetDto>>(saved.Select(ToDto).ToList());
    }

    // ── Report ────────────────────────────────────────────────────────────────────────────────

    public async Task<Result<ExpenseReportDto>> GetReportAsync(string? from, string? to, CancellationToken ct = default)
    {
        if (!ExpenseRecurrence.TryResolveMonths(from, to, Today, DefaultListMonths, OperatingExpenseConstants.MaxReportMonths,
                out var fromMonth, out var toMonth, out var error))
        {
            return Result.Failure<ExpenseReportDto>(error!, ErrorCodes.ValidationError);
        }

        try
        {
            var today = Today;
            var categories = await CategoryDtosAsync(ct);
            var fx = await _fx.GetTableAsync(ct);
            var rows = (await _unitOfWork.OperatingExpenses.ListInRangeAsync(fromMonth, ExpenseRecurrence.LastDayOfMonth(toMonth), ct))
                .Select(row => Convert(row, fx))
                .ToList();
            var budgets = await _unitOfWork.ExpenseBudgets.ListInRangeAsync(fromMonth, toMonth, ct);
            var planned = (await _unitOfWork.OperatingExpenses.ListPlannedDueAsync(today.AddDays(OperatingExpenseConstants.CommitmentHorizonDays), ct))
                .Select(row => Convert(row, fx))
                .ToList();
            var series = (await _unitOfWork.OperatingExpenses.ListSeriesAsync(ct)).Select(row => Convert(row, fx)).ToList();

            var report = ExpenseReportBuilder.Build(
                ExpenseRecurrence.MonthsBetween(fromMonth, toMonth), categories, rows, budgets, planned, series, fx, today, Now);
            return Result.Success(report);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Operating expense report failed. From: {From} To: {To}", fromMonth, toMonth);
            return Result.Failure<ExpenseReportDto>("An unexpected error occurred while building the expense report.", ErrorCodes.InternalServerError);
        }
    }

    // ── P&L ───────────────────────────────────────────────────────────────────────────────────

    public async Task<Result<FinancePnlDto>> GetProfitAndLossAsync(string? from, string? to, string? tz, CancellationToken ct = default)
    {
        if (!ExpenseRecurrence.TryResolveMonths(from, to, Today, 6, OperatingExpenseConstants.MaxPnlMonths,
                out var fromMonth, out var toMonth, out var error))
        {
            return Result.Failure<FinancePnlDto>(error!, ErrorCodes.ValidationError);
        }

        if (!AdminComparisonRange.TryResolveTimeZone(tz, out var zone, out var zoneError))
            return Result.Failure<FinancePnlDto>(zoneError!, ErrorCodes.ValidationError);

        var windowFrom = TimeZoneInfo.ConvertTimeToUtc(fromMonth.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), zone);
        var windowTo = TimeZoneInfo.ConvertTimeToUtc(toMonth.AddMonths(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), zone);
        var pnl = await _insights.GetProfitAndLossAsync(new AdminInsightsQuery { From = windowFrom, To = windowTo, Tz = zone.Id }, ct);
        if (!pnl.IsSuccess) return Result.Failure<FinancePnlDto>(pnl.Error ?? "The revenue and AI cost report failed.", pnl.ErrorCode);

        try
        {
            var categories = await CategoryDtosAsync(ct);
            var fx = await _fx.GetTableAsync(ct);
            var rows = (await _unitOfWork.OperatingExpenses.ListInRangeAsync(fromMonth, ExpenseRecurrence.LastDayOfMonth(toMonth), ct))
                .Select(row => Convert(row, fx))
                .ToList();
            var budgets = await _unitOfWork.ExpenseBudgets.ListInRangeAsync(fromMonth, toMonth, ct);

            return Result.Success(FinancePnlBuilder.Build(
                ExpenseRecurrence.MonthsBetween(fromMonth, toMonth), pnl.Value!, categories, rows, budgets, Now));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Finance P&L failed. From: {From} To: {To}", fromMonth, toMonth);
            return Result.Failure<FinancePnlDto>("An unexpected error occurred while building the profit and loss.", ErrorCodes.InternalServerError);
        }
    }

    // ── CSV import ────────────────────────────────────────────────────────────────────────────

    public async Task<Result<ExpenseImportPreviewDto>> PreviewImportAsync(ExpenseImportRequest request, CancellationToken ct = default)
    {
        var prepared = await PrepareImportAsync(request, ct);
        return prepared.IsSuccess ? Result.Success(prepared.Value!.Preview) : Result.Failure<ExpenseImportPreviewDto>(prepared.Error!, prepared.ErrorCode);
    }

    public async Task<Result<ExpenseImportResultDto>> ImportAsync(ExpenseImportRequest request, Guid actorId, CancellationToken ct = default)
    {
        var prepared = await PrepareImportAsync(request, ct);
        if (!prepared.IsSuccess) return Result.Failure<ExpenseImportResultDto>(prepared.Error!, prepared.ErrorCode);

        var (preview, requests) = prepared.Value!;
        if (preview.InvalidCount > 0 && !request.SkipInvalid)
        {
            return Result.Failure<ExpenseImportResultDto>(
                $"{preview.InvalidCount} row(s) have errors. Fix them, or import only the valid rows.", ErrorCodes.ValidationError);
        }

        if (requests.Count == 0) return Result.Failure<ExpenseImportResultDto>("There is no valid row to import.", ErrorCodes.ValidationError);

        var batchId = Guid.NewGuid();
        var now = Now;
        var categories = (await _unitOfWork.ExpenseCategories.ListOrderedAsync(ct)).ToDictionary(c => c.Id);
        foreach (var save in requests)
        {
            var validation = Validate(save, categories.GetValueOrDefault(save.CategoryId), null);
            var expense = new OperatingExpense { Id = Guid.NewGuid(), CreatedAt = now, CreatedBy = actorId, ImportBatchId = batchId };
            Apply(expense, save, validation, now, actorId, isNew: true);
            await _unitOfWork.OperatingExpenses.AddAsync(expense, ct);
        }

        await _unitOfWork.SaveChangesAsync(ct);
        return Result.Success(new ExpenseImportResultDto(batchId, requests.Count, preview.Rows.Count - requests.Count));
    }

    private sealed record PreparedImport(ExpenseImportPreviewDto Preview, List<SaveOperatingExpenseRequest> Requests);

    private async Task<Result<PreparedImport>> PrepareImportAsync(ExpenseImportRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Csv)) return Result.Failure<PreparedImport>("The file is empty.", ErrorCodes.ValidationError);
        if (request.Csv.Length > 2_000_000) return Result.Failure<PreparedImport>("The file may be at most 2 MB.", ErrorCodes.ValidationError);

        var document = ExpenseCsv.Parse(request.Csv);
        var missing = ExpenseCsv.Required.Where(column => !document.Columns.Contains(column)).ToList();
        if (missing.Count > 0)
            return Result.Failure<PreparedImport>($"Missing column(s): {string.Join(", ", missing)}.", ErrorCodes.ValidationError);
        if (document.Rows.Count == 0) return Result.Failure<PreparedImport>("The file has a header but no rows.", ErrorCodes.ValidationError);
        if (document.Rows.Count > OperatingExpenseConstants.MaxImportRows)
            return Result.Failure<PreparedImport>($"At most {OperatingExpenseConstants.MaxImportRows} rows per import.", ErrorCodes.ValidationError);

        var categories = await _unitOfWork.ExpenseCategories.ListOrderedAsync(ct);
        var bySlug = categories.ToDictionary(c => c.Slug, StringComparer.OrdinalIgnoreCase);
        var byName = categories.GroupBy(c => c.Name.Trim(), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var fx = await _fx.GetTableAsync(ct);

        // Duplicates of what is already recorded: same date, vendor, amount and currency.
        var dates = document.Rows.Select(row => ExpenseCsv.TryParseDate(row.Get(ExpenseCsv.Date), out var d) ? d : (DateOnly?)null)
            .Where(d => d.HasValue).Select(d => d!.Value).ToList();
        var existing = dates.Count == 0
            ? []
            : await _unitOfWork.OperatingExpenses.ListInRangeAsync(dates.Min(), dates.Max(), ct);
        var existingKeys = existing.Select(e => DuplicateKey(e.ExpenseDate, e.Vendor, e.Amount, e.Currency)).ToHashSet(StringComparer.Ordinal);
        var seenInFile = new HashSet<string>(StringComparer.Ordinal);

        var rows = new List<ExpenseImportRowDto>();
        var requests = new List<SaveOperatingExpenseRequest>();
        decimal totalVnd = 0;
        foreach (var row in document.Rows)
        {
            var errors = new List<string>();
            var warnings = new List<string>();

            DateOnly? date = ExpenseCsv.TryParseDate(row.Get(ExpenseCsv.Date), out var parsedDate) ? parsedDate : null;
            if (date is null) errors.Add("date: expected yyyy-MM-dd or dd/MM/yyyy");

            var vendor = row.Get(ExpenseCsv.Vendor);
            if (vendor is null) errors.Add("vendor: required");

            var categoryText = row.Get(ExpenseCsv.Category);
            ExpenseCategory? category = null;
            if (categoryText is null) errors.Add("category: required");
            else if (!bySlug.TryGetValue(categoryText, out category) && !byName.TryGetValue(categoryText, out category))
                errors.Add($"category: '{categoryText}' is not a known category");

            decimal? amount = ExpenseCsv.TryParseAmount(row.Get(ExpenseCsv.Amount), out var parsedAmount) ? parsedAmount : null;
            if (amount is null) errors.Add("amount: expected a number such as 1500000 or 1,500,000 or 12.50");

            var currency = ExpenseCsv.NormalizeCurrency(row.Get(ExpenseCsv.Currency));
            if (currency is null) errors.Add("currency: must be VND or USD");

            var status = row.Get(ExpenseCsv.Status)?.ToLowerInvariant() ?? OperatingExpenseConstants.Statuses.Paid;
            var recurrence = row.Get(ExpenseCsv.Recurrence)?.ToLowerInvariant() ?? OperatingExpenseConstants.Recurrences.None;
            var tags = ExpenseCsv.ParseTags(row.Get(ExpenseCsv.Tags));
            var save = new SaveOperatingExpenseRequest(
                date ?? default, vendor ?? string.Empty, category?.Id ?? Guid.Empty, row.Get(ExpenseCsv.Description),
                amount ?? 0, currency ?? string.Empty, row.Get(ExpenseCsv.PaymentMethod), status, row.Get(ExpenseCsv.PaidBy),
                tags, recurrence, null);

            if (errors.Count == 0)
            {
                var validation = Validate(save, category, null);
                if (validation.Error is not null) errors.Add(validation.Error);
            }

            decimal? amountVnd = null;
            if (errors.Count == 0)
            {
                var key = DuplicateKey(save.ExpenseDate, save.Vendor, save.Amount, save.Currency);
                if (existingKeys.Contains(key)) warnings.Add("An expense with the same date, vendor and amount is already recorded.");
                if (!seenInFile.Add(key)) warnings.Add("The same date, vendor and amount appear earlier in this file.");

                amountVnd = ToVnd(save.Amount, save.Currency, save.ExpenseDate, fx).Vnd;
                if (amountVnd is null) warnings.Add("No USD→VND rate is recorded for this date; the amount stays out of VND totals until one is.");
                totalVnd += amountVnd ?? 0;
                requests.Add(save);
            }

            rows.Add(new ExpenseImportRowDto(
                row.Line, errors.Count == 0, errors, warnings, date, vendor, category?.Id, category?.Name, amount, currency,
                amountVnd, status, save.PaymentMethod, save.PaidBy, tags, save.Description, recurrence));
        }

        var preview = new ExpenseImportPreviewDto(
            document.Columns, document.UnknownColumns, rows, rows.Count(r => r.Valid), rows.Count(r => !r.Valid), totalVnd);
        return Result.Success(new PreparedImport(preview, requests));
    }

    private static string DuplicateKey(DateOnly date, string vendor, decimal amount, string currency)
        => string.Create(CultureInfo.InvariantCulture, $"{date:yyyy-MM-dd}|{vendor.Trim().ToLowerInvariant()}|{amount:0.##}|{currency}");

    // ── Recurrence ────────────────────────────────────────────────────────────────────────────

    public async Task<int> GenerateDueOccurrencesAsync(DateOnly today, CancellationToken ct = default)
    {
        var dueBy = today.AddDays(OperatingExpenseConstants.RecurrenceLeadDays);
        var series = await _unitOfWork.OperatingExpenses.ListSeriesDueAsync(dueBy, ct);
        var written = 0;
        foreach (var source in series)
        {
            var created = 0;
            var guard = 0;
            while (source.NextDueDate is { } due && due <= dueBy && guard++ < 36)
            {
                if (source.RecurrenceEndDate is { } end && due > end) break;
                if (!await _unitOfWork.OperatingExpenses.OccurrenceExistsAsync(source.Id, due, ct))
                {
                    var now = Now;
                    await _unitOfWork.OperatingExpenses.AddAsync(new OperatingExpense
                    {
                        Id = Guid.NewGuid(),
                        ExpenseDate = due,
                        Vendor = source.Vendor,
                        CategoryId = source.CategoryId,
                        Description = source.Description,
                        Amount = source.Amount,
                        Currency = source.Currency,
                        PaymentMethod = source.PaymentMethod,
                        Status = OperatingExpenseConstants.Statuses.Planned,
                        PaidBy = source.PaidBy,
                        Tags = [.. source.Tags],
                        Recurrence = OperatingExpenseConstants.Recurrences.None,
                        RecurringSourceId = source.Id,
                        CreatedAt = now,
                        UpdatedAt = now,
                    }, ct);
                    created++;
                }

                source.NextDueDate = ExpenseRecurrence.NextAfter(source.ExpenseDate, source.Recurrence, due);
                source.UpdatedAt = Now;
            }

            try
            {
                await _unitOfWork.SaveChangesAsync(ct);
                written += created;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Another replica wrote the same occurrence first (unique index): nothing is lost.
                _logger.LogInformation(ex, "Recurring expense {SeriesId}: occurrence already written elsewhere; skipped.", source.Id);
                _unitOfWork.ClearTracking();
            }
        }

        return written;
    }

    // ── Conversion and mapping ────────────────────────────────────────────────────────────────

    public sealed record Converted(OperatingExpense Expense, decimal? Vnd, FxRateResolution? Fx);

    public static Converted Convert(OperatingExpense expense, FxRateTable fx)
    {
        var (vnd, resolution) = ToVnd(expense.Amount, expense.Currency, expense.ExpenseDate, fx);
        return new Converted(expense, vnd, resolution);
    }

    public static (decimal? Vnd, FxRateResolution? Fx) ToVnd(decimal amount, string currency, DateOnly date, FxRateTable fx)
    {
        if (currency == OperatingExpenseConstants.Currencies.Vnd) return (amount, null);
        var resolution = fx.Resolve(date);
        return resolution.Rate is { } rate
            ? (decimal.Round(amount * rate, 0, MidpointRounding.AwayFromZero), resolution)
            : (null, resolution);
    }

    public static string? Describe(IEnumerable<Converted> rows)
        => FxRateTable.Describe(rows.Where(r => r.Fx is not null).Select(r => r.Fx!.Value));

    private static ExpenseTotalsDto Totals(IReadOnlyCollection<Converted> rows)
        => new(
            rows.Count,
            rows.Sum(r => r.Vnd ?? 0),
            rows.Where(r => r.Expense.Status == OperatingExpenseConstants.Statuses.Paid).Sum(r => r.Vnd ?? 0),
            rows.Where(r => r.Expense.Status == OperatingExpenseConstants.Statuses.Planned).Sum(r => r.Vnd ?? 0),
            rows.Count(r => r.Vnd is null));

    public static OperatingExpenseDto ToDto(Converted converted)
    {
        var e = converted.Expense;
        return new OperatingExpenseDto(
            e.Id,
            e.ExpenseDate,
            e.Vendor,
            e.CategoryId,
            e.Category?.Slug ?? string.Empty,
            e.Category?.Name ?? string.Empty,
            e.Category?.Color,
            e.Description,
            e.Amount,
            e.Currency,
            converted.Vnd,
            converted.Fx?.Rate,
            converted.Fx?.Source,
            converted.Fx?.RateDate,
            e.PaymentMethod,
            e.Status,
            e.PaidAt is { } paid ? DateTime.SpecifyKind(paid, DateTimeKind.Utc) : null,
            e.PaidBy,
            e.Tags,
            e.Recurrence,
            e.NextDueDate,
            e.RecurrenceEndDate,
            e.RecurringSourceId,
            e.ReceiptStorageKey is null ? null : new ExpenseReceiptDto(e.ReceiptFileName ?? "receipt", e.ReceiptContentType, e.ReceiptSizeBytes),
            e.ImportBatchId,
            DateTime.SpecifyKind(e.CreatedAt, DateTimeKind.Utc),
            DateTime.SpecifyKind(e.UpdatedAt, DateTimeKind.Utc));
    }

    public static ExpenseCategoryDto ToDto(ExpenseCategory category, bool inUse)
        => new(category.Id, category.Slug, category.Name, category.Description, category.Color, category.SortOrder, category.IsActive, inUse);

    private static ExpenseBudgetDto ToDto(ExpenseBudget budget)
        => new(budget.CategoryId, ExpenseRecurrence.MonthKey(budget.Month), budget.AmountVnd, budget.Note);

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
