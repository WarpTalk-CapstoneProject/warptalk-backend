using System;
using System.Collections.Generic;

namespace WarpTalk.BillingService.Domain.Interfaces;

public sealed record PageRequest(int PageNumber, int PageSize);

public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    int TotalCount,
    int PageNumber,
    int PageSize);

/// <param name="Search">
/// Global ledger only. A GUID matches Id or ReferenceId exactly; any other text is an ILIKE
/// substring match on Description.
/// </param>
/// <param name="Sort">created_desc (default) | created_asc | amount_desc | amount_asc (magnitude).</param>
public sealed record CreditTransactionHistoryFilter(
    PageRequest Page,
    IReadOnlyCollection<Guid>? SubscriptionIds = null,
    Guid? WorkspaceId = null,
    string? Type = null,
    DateTime? FromDate = null,
    DateTime? ToDate = null,
    int? MinAmount = null,
    int? MaxAmount = null,
    string? Search = null,
    string Sort = CreditHistorySorts.CreatedDesc);

public static class CreditHistorySorts
{
    public const string CreatedDesc = "created_desc";
    public const string CreatedAsc = "created_asc";
    public const string AmountDesc = "amount_desc";
    public const string AmountAsc = "amount_asc";

    public static readonly string[] All = [CreatedDesc, CreatedAsc, AmountDesc, AmountAsc];
}

/// <summary>
/// The platform-wide invoice list. Every value is validated and normalized by the service
/// (status lowercase, currency uppercase, dates UTC) before it reaches the repository.
/// </summary>
/// <param name="Search">A GUID matches the invoice Id exactly; other text is an ILIKE match on invoice_number.</param>
/// <param name="FromDate">Inclusive lower bound on issued_at.</param>
/// <param name="ToDate">Exclusive upper bound on issued_at.</param>
/// <param name="MinTotal">Inclusive lower bound on total.</param>
/// <param name="MaxTotal">Inclusive upper bound on total.</param>
public sealed record GlobalInvoiceFilter(
    PageRequest Page,
    string? Search = null,
    string? Status = null,
    Guid? WorkspaceId = null,
    string? Currency = null,
    DateTime? FromDate = null,
    DateTime? ToDate = null,
    decimal? MinTotal = null,
    decimal? MaxTotal = null,
    string Sort = GlobalInvoiceSorts.IssuedDesc);

public static class GlobalInvoiceSorts
{
    public const string IssuedDesc = "issued_desc";
    public const string IssuedAsc = "issued_asc";
    public const string TotalDesc = "total_desc";
    public const string TotalAsc = "total_asc";
    public const string DueAsc = "due_asc";

    public static readonly string[] All = [IssuedDesc, IssuedAsc, TotalDesc, TotalAsc, DueAsc];
}
