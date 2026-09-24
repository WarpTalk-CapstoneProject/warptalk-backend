using System;
using WarpTalk.BillingService.Domain.Constants;

namespace WarpTalk.BillingService.Domain.Entities;

public partial class Invoice
{
    /// <summary>
    /// Records the invoice as settled: the invoice and the payment it was raised against both move
    /// to paid at <paramref name="paidAt"/>. The single definition of "mark paid" — the older
    /// invoice endpoint and the admin workspace page both go through here, so the two cannot drift
    /// into settling different columns. The payment must be loaded.
    /// </summary>
    public void MarkPaid(DateTime paidAt)
    {
        Status = InvoiceConstants.InvoiceStatuses.Paid;
        PaidAt = paidAt;
        Payment.Status = PaymentConstants.PaymentStatuses.Paid;
        Payment.PaidAt = paidAt;
        Payment.UpdatedAt = paidAt;
    }
}
