using System;

namespace WarpTalk.BillingService.Application.DTOs;

public record PaginationQuery(
    int PageNumber = 1,
    int PageSize = 20
);

public record CreditHistoryQuery(
    Guid? WorkspaceId = null,
    string? Type = null,
    DateTime? FromDate = null,
    DateTime? ToDate = null,
    int? MinAmount = null,
    int? MaxAmount = null
) : PaginationQuery;

/// <summary>
/// The platform-wide credit ledger (GET api/v1/credits/history/global). Everything the
/// workspace-scoped history accepts, plus search and sort — which live HERE rather than on
/// <see cref="CreditHistoryQuery"/> so the workspace endpoint's contract does not change.
/// </summary>
public record GlobalCreditHistoryQuery : CreditHistoryQuery
{
    /// <summary>
    /// A GUID matches the transaction id or its reference_id exactly; anything else is a
    /// case-insensitive substring match on the description.
    /// </summary>
    public string? Search { get; init; }

    /// <summary>
    /// created_desc (default, today's order) | created_asc | amount_desc | amount_asc. Amount
    /// sorts on the magnitude, like minAmount/maxAmount, so the largest debits and credits sit
    /// together. Anything else is a 400.
    /// </summary>
    public string? Sort { get; init; }
}

/// <summary>
/// The platform-wide invoice list (GET api/v1/invoices/global). Extends
/// <see cref="PaginationQuery"/> so pageNumber/pageSize bind exactly as before.
/// </summary>
public record GlobalInvoiceQuery : PaginationQuery
{
    /// <summary>
    /// A GUID matches the invoice id exactly; anything else is a case-insensitive substring match
    /// on the invoice number.
    /// </summary>
    public string? Search { get; init; }

    /// <summary>draft | issued | open | paid | void | uncollectible, or all / null for every status.</summary>
    public string? Status { get; init; }

    /// <summary>Only invoices whose payment's subscription belongs to this workspace.</summary>
    public Guid? WorkspaceId { get; init; }

    /// <summary>ISO 4217 code (three letters), case-insensitive.</summary>
    public string? Currency { get; init; }

    /// <summary>Inclusive lower bound on issued_at (UTC; offset-less values are read as UTC).</summary>
    public DateTime? FromDate { get; init; }

    /// <summary>Exclusive upper bound on issued_at (UTC). Earlier than fromDate is a 400.</summary>
    public DateTime? ToDate { get; init; }

    /// <summary>Inclusive lower bound on total.</summary>
    public decimal? MinTotal { get; init; }

    /// <summary>Inclusive upper bound on total. Below minTotal is a 400.</summary>
    public decimal? MaxTotal { get; init; }

    /// <summary>
    /// issued_desc (default) | issued_asc | total_desc | total_asc | due_asc. The issued_* keys
    /// order on created_at — the column the list has always been ordered by, stamped with the
    /// same instant as issued_at on insert — so the default is exactly today's order. due_asc
    /// puts invoices without a due date last.
    /// </summary>
    public string? Sort { get; init; }
}

public record BillingReportQuery(
    int Month,
    int Year
);

public record UsageChartQuery(
    int Year,
    int Days = 30,
    int Limit = 5
);
