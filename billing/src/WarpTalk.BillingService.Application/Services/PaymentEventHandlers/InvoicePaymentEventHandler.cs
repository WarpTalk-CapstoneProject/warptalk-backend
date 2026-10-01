using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Services.PaymentEventHandlers;

/// <summary>
/// WT-878 — paying an invoice by card lifts the <c>invoice_overdue</c> suspension it paid for.
///
/// "InvoicePayment" is the type InvoiceService.CreateInvoiceCheckoutSessionAsync puts on the Stripe
/// session, and no handler claimed it: PaymentAppService logged <c>payment_type_unhandled</c>,
/// marked the payment and the invoice paid through its generic tail, and left the workspace the
/// overdue sweeper had suspended still suspended — the main production path, since the only seeded
/// plan (Enterprise) bills by invoice.
///
/// WHICH INVOICE
///   The checkout wrote the session id onto the invoice's own payment row
///   (Payment.ProviderTransactionId), so PaymentAppService arrives here with that row as
///   <see cref="PaymentEventContext.ExistingPayment"/>, and the invoice is the one raised against it.
///
/// IDEMPOTENCE
///   A redelivered event whose payment is already paid never reaches a handler (PaymentAppService's
///   already-processed guard). Should one arrive anyway, settlement is a no-op on a paid invoice and
///   the lift rule only ever moves a suspended row to resumed.
///
/// Registered AFTER CancellationPaymentEventHandler: a cancelled or refunded invoice payment keeps
/// the behaviour it had. Every other status of this type is claimed, and only Paid does anything.
/// </summary>
public sealed class InvoicePaymentEventHandler : IPaymentEventHandler
{
    private readonly IInvoiceService _invoiceService;
    private readonly ILogger<InvoicePaymentEventHandler> _logger;
    private readonly ISuspensionLiftService? _suspensionLift;

    public InvoicePaymentEventHandler(
        IInvoiceService invoiceService,
        ILogger<InvoicePaymentEventHandler> logger,
        ISuspensionLiftService? suspensionLift = null)
    {
        _invoiceService = invoiceService;
        _logger = logger;
        _suspensionLift = suspensionLift;
    }

    public bool CanHandle(PaymentEventContext context)
        => string.Equals(
            context.Request.PaymentType,
            PaymentConstants.PaymentTypes.InvoicePayment,
            StringComparison.OrdinalIgnoreCase);

    public async Task<Result> HandleAsync(PaymentEventContext context, CancellationToken cancellationToken = default)
    {
        if (context.ParsedPaymentStatus != PaymentConstants.PaymentStatuses.Paid)
        {
            return Result.Success();
        }

        if (context.ExistingPayment is not { } payment)
        {
            // The session no longer points at an invoice's payment — most likely a second checkout
            // for the same invoice overwrote the stored session id before the first was paid. The
            // money is recorded by the caller either way; the lift needs a person.
            _logger.LogError(
                "invoice_payment_unmatched: StripeSessionId={SessionId} WorkspaceId={WorkspaceId} Amount={Amount} {Currency}. "
                + "No invoice payment carries this session; the invoice was NOT settled and no suspension was lifted.",
                context.Request.StripeSessionId,
                context.WorkspaceId,
                context.Request.Amount,
                context.Request.Currency);
            return Result.Success();
        }

        var settlement = await _invoiceService.StageInvoicePaidForPaymentAsync(payment.Id, DateTime.UtcNow, cancellationToken);
        if (settlement is null)
        {
            _logger.LogError(
                "invoice_payment_unmatched: PaymentId={PaymentId} StripeSessionId={SessionId} WorkspaceId={WorkspaceId}. "
                + "The payment has no invoice; no suspension was lifted.",
                payment.Id,
                context.Request.StripeSessionId,
                context.WorkspaceId);
            return Result.Success();
        }

        // Not SubscriptionChanged: the caller would announce a "subscription started" to the owner.
        // The AI push the caller makes for that flag is done here instead, only when something lifted.
        if (settlement.Lift.Lifted && _suspensionLift is { } liftService)
        {
            var subscription = settlement.Subscription;
            context.AfterCommit.Add(async ct =>
            {
                await liftService.PushServiceStateAsync(subscription, ct);
                await liftService.PublishCreditsUpdatedAsync(subscription, resumed: true, ct);
            });
        }

        _logger.LogInformation(
            "invoice_payment_settled: InvoiceId={InvoiceId} NewlyPaid={NewlyPaid} WorkspaceId={WorkspaceId} Lifted={Lifted}",
            settlement.Invoice.Id,
            settlement.NewlyPaid,
            context.WorkspaceId,
            settlement.Lift.LiftedReason);

        return Result.Success();
    }
}
