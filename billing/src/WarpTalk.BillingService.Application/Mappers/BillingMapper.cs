using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Domain.Entities;

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
        plan.SortOrder
    );

    public static SubscriptionDto ToDto(this Subscription sub, string planName) => new(
        sub.Id,
        sub.WorkspaceId,
        sub.PlanId,
        planName,
        sub.Status,
        sub.CreditsRemaining,
        sub.CreditsUsedThisCycle,
        sub.CurrentPeriodStart,
        sub.CurrentPeriodEnd,
        sub.AutoRenew,
        sub.CreatedAt,
        sub.CancelledAt
    );

    public static Subscription ToEntity(this CreateSubscriptionRequest request, Plan plan)
    {
        var now = DateTime.UtcNow;
        return new Subscription
        {
            Id = Guid.NewGuid(),
            UserId = request.UserId,
            WorkspaceId = request.WorkspaceId,
            PlanId = request.PlanId,
            Status = "active",
            CreditsRemaining = plan.CreditsPerCycle,
            CreditsUsedThisCycle = 0,
            CurrentPeriodStart = now,
            CurrentPeriodEnd = now.AddMonths(1),
            AutoRenew = true,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    public static Subscription ToEntity(this ChangeSubscriptionRequest request, Subscription oldSub, Plan newPlan)
    {
        var now = DateTime.UtcNow;
        return new Subscription
        {
            Id = Guid.NewGuid(),
            UserId = oldSub.UserId,
            WorkspaceId = oldSub.WorkspaceId,
            PlanId = newPlan.Id,
            Status = "active",
            CreditsRemaining = newPlan.CreditsPerCycle + oldSub.CreditsRemaining,
            CreditsUsedThisCycle = 0,
            CurrentPeriodStart = now,
            CurrentPeriodEnd = now.AddMonths(1),
            AutoRenew = true,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    public static void Cancel(this Subscription sub, string? reason)
    {
        var now = DateTime.UtcNow;
        sub.Status = "cancelled";
        sub.CancellationReason = reason;
        sub.CancelledAt = now;
        sub.IsActive = false;
        sub.UpdatedAt = now;
    }

    public static CreditBalanceDto ToCreditBalanceDto(this Subscription sub, Guid workspaceId) => new(
        workspaceId,
        sub.CreditsRemaining,
        sub.CreditsUsedThisCycle,
        sub.Status,
        sub.CurrentPeriodStart,
        sub.CurrentPeriodEnd
    );

    public static CreditTransactionDto ToDto(this CreditTransaction tx) => new(
        tx.Id,
        tx.Amount,
        tx.Type,
        tx.Description,
        tx.ReferenceType,
        tx.ReferenceId,
        tx.BalanceAfter,
        tx.CreatedAt
    );

    public static CreditTransaction ToEntity(this ConsumeCreditsRequest request, Subscription sub) => new()
    {
        Id = Guid.NewGuid(),
        SubscriptionId = sub.Id,
        UserId = sub.UserId,
        Amount = -request.Amount,
        Type = "consumption",
        ReferenceType = request.ReferenceType,
        ReferenceId = request.ReferenceId,
        BalanceAfter = sub.CreditsRemaining,
        CreatedAt = DateTime.UtcNow
    };

    public static CreditTransaction ToEntity(this TopUpRequest request, Subscription sub) => new()
    {
        Id = Guid.NewGuid(),
        SubscriptionId = sub.Id,
        UserId = sub.UserId,
        Amount = request.Amount,
        Type = "top_up",
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
}
