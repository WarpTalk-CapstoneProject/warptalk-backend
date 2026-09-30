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
    Task<Result<PaginatedResponse<InvoiceDto>>> GetGlobalInvoicesAsync(GlobalInvoiceQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// A Stripe checkout URL for one open invoice. The caller is authorised against the workspace
    /// the INVOICE belongs to — the route carries only an invoice id, so there is nothing else to
    /// check against — and becomes the buyer on the session (WT-545).
    /// </summary>
    Task<Result<string>> CreateInvoiceCheckoutSessionAsync(
        Guid invoiceId,
        InvoiceCheckoutCaller caller,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Admin "mark paid". Idempotent; since WT-878 it also runs the suspension-lift rule
    /// (<see cref="ISuspensionLiftService"/>) and, when that lifts, pushes the state to AI.
    /// </summary>
    Task<Result<InvoiceDto>> MarkInvoicePaidAsync(Guid invoiceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// WT-878: stages (does NOT save) the settlement of the invoice raised against
    /// <paramref name="paymentId"/> — invoice and payment to paid, unless already — and the
    /// invoice_overdue lift that settlement earns. For the Stripe payment path, which commits it with
    /// the payment row. Null when no invoice was raised against that payment.
    /// </summary>
    Task<InvoiceSettlement?> StageInvoicePaidForPaymentAsync(
        Guid paymentId, DateTime paidAtUtc, CancellationToken cancellationToken = default);
}

/// <summary>
/// WT-878: one staged invoice settlement. <see cref="NewlyPaid"/> is false on a replay (already
/// paid); <see cref="Lift"/> is what the suspension-lift rule did to <see cref="Subscription"/>.
/// </summary>
public sealed record InvoiceSettlement(
    WarpTalk.BillingService.Domain.Entities.Invoice Invoice,
    bool NewlyPaid,
    WarpTalk.BillingService.Domain.Entities.Subscription Subscription,
    SuspensionLiftOutcome Lift);
