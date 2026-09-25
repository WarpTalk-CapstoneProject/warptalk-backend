using System;
using System.Collections.Generic;

namespace WarpTalk.BillingService.Domain.Entities;

/// <summary>
/// subscription.operating_expenses (G12): one company operating expense, in its own currency (VND or
/// USD). Reports convert a USD row at the USD→VND rate of its <see cref="ExpenseDate"/>; no VND
/// figure is stored.
///
/// A row whose <see cref="Recurrence"/> is monthly or yearly is also a series: ExpenseRecurrenceWorker
/// writes each next occurrence as a planned row (its <see cref="RecurringSourceId"/> = this row)
/// shortly before <see cref="NextDueDate"/> and then advances it.
/// </summary>
public class OperatingExpense
{
    public Guid Id { get; set; }
    public DateOnly ExpenseDate { get; set; }
    public string Vendor { get; set; } = string.Empty;
    public Guid CategoryId { get; set; }
    public ExpenseCategory? Category { get; set; }
    public string? Description { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "VND";
    public string? PaymentMethod { get; set; }
    public string Status { get; set; } = "paid";
    public DateTime? PaidAt { get; set; }
    public string? PaidBy { get; set; }
    public List<string> Tags { get; set; } = [];
    public string Recurrence { get; set; } = "none";
    public DateOnly? NextDueDate { get; set; }
    public DateOnly? RecurrenceEndDate { get; set; }
    public Guid? RecurringSourceId { get; set; }
    public string? ReceiptStorageKey { get; set; }
    public string? ReceiptFileName { get; set; }
    public string? ReceiptContentType { get; set; }
    public long? ReceiptSizeBytes { get; set; }
    public Guid? ImportBatchId { get; set; }
    public Guid? CreatedBy { get; set; }
    public Guid? UpdatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }

    public bool IsSeries => Recurrence is "monthly" or "yearly";
}
