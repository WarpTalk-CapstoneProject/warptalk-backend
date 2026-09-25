using System;

namespace WarpTalk.BillingService.Domain.Entities;

/// <summary>subscription.expense_categories (G12): the managed list operating expenses are filed under.</summary>
public class ExpenseCategory
{
    public Guid Id { get; set; }
    public string Slug { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>A chart token name (blue, cyan, violet, amber, green, pink, gray…), not a hex value.</summary>
    public string? Color { get; set; }

    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }
}
