using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Application.Interfaces;

namespace WarpTalk.BillingService.Application.Mappers;

public static class BillingMapper
{
    public static PlanDto ToDto(this Plan plan) => new(
        plan.Id,
        plan.Name,
        plan.Slug,
        plan.Tier,
        plan.Price,
        plan.Currency,
        plan.BillingCycle,
        plan.CreditsPerCycle,
        plan.MaxParticipants,
        plan.MaxLanguages,
        plan.VoiceCloneEnabled,
        plan.AiAssistantEnabled,
        plan.GlossaryEnabled,
        plan.DedicatedGpu,
        plan.Features,
        plan.SortOrder,
        plan.IsActive
    );

    public static Plan ToEntity(this PlanRequest request) => new()
    {
        Id = Guid.NewGuid(),
        Name = request.Name,
        Slug = request.Slug.ToLowerInvariant().Trim(),
        Tier = request.Tier,
        Price = request.Price,
        Currency = request.Currency,
        BillingCycle = request.BillingCycle,
        CreditsPerCycle = request.CreditsPerCycle,
        MaxParticipants = request.MaxParticipants,
        MaxLanguages = request.MaxLanguages,
        VoiceCloneEnabled = request.VoiceCloneEnabled,
        AiAssistantEnabled = request.AiAssistantEnabled,
        GlossaryEnabled = request.GlossaryEnabled,
        DedicatedGpu = request.DedicatedGpu,
        Features = request.Features,
        SortOrder = request.SortOrder,
        IsActive = request.IsActive,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    public static void UpdateFromRequest(this Plan plan, PlanRequest request)
    {
        plan.Name = request.Name;
        plan.Slug = request.Slug.ToLowerInvariant().Trim();
        plan.Tier = request.Tier;
        plan.Price = request.Price;
        plan.Currency = request.Currency;
        plan.BillingCycle = request.BillingCycle;
        plan.CreditsPerCycle = request.CreditsPerCycle;
        plan.MaxParticipants = request.MaxParticipants;
        plan.MaxLanguages = request.MaxLanguages;
        plan.VoiceCloneEnabled = request.VoiceCloneEnabled;
        plan.AiAssistantEnabled = request.AiAssistantEnabled;
        plan.GlossaryEnabled = request.GlossaryEnabled;
        plan.DedicatedGpu = request.DedicatedGpu;
        plan.Features = request.Features;
        plan.SortOrder = request.SortOrder;
        plan.IsActive = request.IsActive;
        plan.UpdatedAt = DateTime.UtcNow;
    }

    public static SubscriptionDto ToDto(this Subscription sub, string planName, decimal price) => new(
        sub.Id,
        sub.UserId,
        sub.WorkspaceId,
        sub.PlanId,
        planName,
        price,
        sub.Status,
        sub.CreditsRemaining,
        sub.CreditsUsedThisCycle,
        sub.CurrentPeriodStart,
        sub.CurrentPeriodEnd,
        sub.AutoRenew,
        !sub.AutoRenew,
        sub.CreatedAt,
        sub.CancelledAt
    );

    public static Subscription ToEntity(this SubscriptionRequest request, Plan plan)
    {
        var now = DateTime.UtcNow;
        return new Subscription
        {
            Id = Guid.NewGuid(),
            UserId = request.UserId ?? Guid.Empty,
            WorkspaceId = request.WorkspaceId,
            PlanId = request.PlanId,
            Status = "pending",
            CreditsRemaining = 0,
            CreditsUsedThisCycle = 0,
            CurrentPeriodStart = now,
            CurrentPeriodEnd = now, // Will be updated on activation
            AutoRenew = true,
            IsActive = false,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    public static Subscription ToEntity(this SubscriptionRequest request, Subscription oldSub, Plan newPlan)
    {
        var now = DateTime.UtcNow;
        return new Subscription
        {
            Id = Guid.NewGuid(),
            UserId = oldSub.UserId,
            WorkspaceId = oldSub.WorkspaceId,
            PlanId = newPlan.Id,
            Status = "pending",
            CreditsRemaining = oldSub.CreditsRemaining, // Only carry over old for now, new plan credits added on webhook
            CreditsUsedThisCycle = 0,
            CurrentPeriodStart = now,
            CurrentPeriodEnd = now,
            AutoRenew = true,
            IsActive = false,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    /// <summary>
    /// Marks the subscription to cancel at the end of the current billing period.
    /// The subscription remains active (IsActive = true) until CurrentPeriodEnd.
    /// </summary>
    public static void Cancel(this Subscription sub, string? reason)
    {
        var now = DateTime.UtcNow;
        sub.CancellationReason = reason;
        sub.AutoRenew = false;
        sub.Status = "cancelled";
        sub.UpdatedAt = now;
        // Status stays "active" — user can use until CurrentPeriodEnd
        // IsActive stays true — scheduler will deactivate it at period end
    }

    /// <summary>
    /// Immediately cancels the subscription (used internally for upgrade/downgrade flow).
    /// </summary>
    public static void CancelImmediately(this Subscription sub, string? reason)
    {
        var now = DateTime.UtcNow;
        sub.Status = "cancelled";
        sub.CancellationReason = reason;
        sub.CancelledAt = now;
        sub.AutoRenew = false;
        sub.IsActive = false;
        sub.UpdatedAt = now;
    }

    public static CreditBalanceDto ToCreditBalanceDto(this Subscription sub, Guid workspaceId) => new(
        workspaceId,
        sub.CreditsRemaining,
        sub.CreditsUsedThisCycle,
        sub.CreditsRemaining + sub.CreditsUsedThisCycle,
        sub.Status.ToString(),
        sub.CurrentPeriodStart,
        sub.CurrentPeriodEnd
    );

    public static CreditTransactionDto ToDto(this CreditTransaction tx) => new(
        tx.Id,
        tx.Amount,
        tx.Type.ToString(),
        tx.Description,
        tx.ReferenceType,
        tx.ReferenceId,
        tx.BalanceAfter,
        tx.CreatedAt
    );

    public static CreditReservationDto ToDto(this RedisCreditReservation res) => new(
        Guid.Empty,
        res.SubscriptionId,
        res.IdempotencyKey,
        res.Amount,
        "Reserved",
        DateTime.UtcNow.AddMinutes(5)
    );

    public static CreditTransaction ToEntity(this ConsumeCreditsRequest request, Subscription sub) => new()
    {
        Id = Guid.NewGuid(),
        SubscriptionId = sub.Id,
        UserId = sub.UserId,
        Amount = -request.Amount,
        Type = "consume",
        ReferenceType = request.ReferenceType,
        ReferenceId = request.ReferenceId,
        BalanceAfter = sub.CreditsRemaining,
        CreatedAt = DateTime.UtcNow
    };

    public static PaymentTransactionDto ToDto(this Payment payment) => new(
        payment.Id,
        payment.SubscriptionId,
        payment.Amount,
        payment.TaxAmount,
        payment.TotalAmount,
        payment.Currency,
        payment.PaymentMethod,
        payment.Provider,
        payment.ProviderTransactionId,
        payment.ProviderOrderId,
        payment.Status,
        payment.FailureReason,
        payment.PaidAt,
        payment.CreatedAt
    );

    public static Payment ToEntity(this CreatePaymentRequest request, decimal amount, string currency, decimal taxAmount = 0)
    {
        var now = DateTime.UtcNow;
        return new Payment
        {
            Id = Guid.NewGuid(),
            SubscriptionId = request.SubscriptionId,
            UserId = request.UserId,
            Amount = amount,
            TaxAmount = taxAmount,
            TotalAmount = amount + taxAmount,
            Currency = currency,
            PaymentMethod = request.PaymentMethod,
            Provider = request.Provider,
            Status = "pending",
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    public static CreditTransaction ToCreditTransaction(this RecordUsageRequest request, Subscription sub) => new()
    {
        Id = Guid.NewGuid(),
        SubscriptionId = sub.Id,
        UserId = request.UserId,
        Amount = -request.CreditsConsumed, // Negative for consumption
        Type = "consume",
        Description = $"AI Usage: {request.UsageType} by User {request.UserId}",
        ReferenceType = "usage_record",
        ReferenceId = request.TranslationRoomId,
        BalanceAfter = sub.CreditsRemaining,
        CreatedAt = DateTime.UtcNow
    };

    public static UsageRecord ToUsageRecord(this RecordUsageRequest request, Subscription sub) => new()
    {
        Id = Guid.NewGuid(),
        SubscriptionId = sub.Id,
        UserId = request.UserId,
        WorkspaceId = request.HostWorkspaceId,
        TranslationRoomId = request.TranslationRoomId,
        SegmentId = request.SegmentId,
        UsageType = request.UsageType,
        Unit = request.Unit,
        Quantity = request.Quantity,
        CreditsConsumed = request.CreditsConsumed,
        DurationSeconds = request.DurationSeconds,
        Details = request.Details,
        RecordedAt = DateTime.UtcNow
    };
}
