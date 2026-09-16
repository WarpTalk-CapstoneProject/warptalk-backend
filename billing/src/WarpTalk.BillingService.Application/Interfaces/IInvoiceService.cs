using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Interfaces;

/// <summary>
/// Who is asking to pay, read from the token by the API — never from the request.
/// <paramref name="IsPlatformAdmin"/> is the JWT platform role, which genuinely is a claim.
/// </summary>
public record InvoiceCheckoutCaller(Guid UserId, string Email, bool IsPlatformAdmin);

public interface IInvoiceService
{
    Task<Result<PaginatedResponse<InvoiceDto>>> GetInvoicesAsync(Guid workspaceId, PaginationQuery query, CancellationToken cancellationToken = default);
    Task<Result<PaginatedResponse<InvoiceDto>>> GetGlobalInvoicesAsync(PaginationQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// A Stripe checkout URL for one open invoice. The caller is authorised against the workspace
    /// the INVOICE belongs to — the route carries only an invoice id, so there is nothing else to
    /// check against — and becomes the buyer on the session (WT-545).
    /// </summary>
    Task<Result<string>> CreateInvoiceCheckoutSessionAsync(
        Guid invoiceId,
        InvoiceCheckoutCaller caller,
        CancellationToken cancellationToken = default);

    Task<Result<InvoiceDto>> MarkInvoicePaidAsync(Guid invoiceId, CancellationToken cancellationToken = default);
}
