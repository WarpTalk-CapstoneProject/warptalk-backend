using System;

namespace WarpTalk.BillingService.Domain.Entities;

/// <summary>subscription.expense_budgets (G12): the VND budget of one category for one month.</summary>
public class ExpenseBudget
{
    public Guid Id { get; set; }
    public Guid CategoryId { get; set; }

    /// <summary>The first day of the month.</summary>
    public DateOnly Month { get; set; }

    public decimal AmountVnd { get; set; }
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }
}
