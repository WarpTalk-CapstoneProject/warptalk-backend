using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Application.Mappers;

public static class CreditMapper
{
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

    public static CreditTransaction ToEntity(this ConsumeCreditsRequest request, Subscription sub) => new()
    {
        Id = Guid.NewGuid(),
        SubscriptionId = sub.Id,
        UserId = sub.UserId,
        Amount = -request.Amount,
        Type = "consume",
        ReferenceType = request.ReferenceType.ToString(),
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
        ReferenceType = request.ReferenceType.ToString(),
        ReferenceId = request.ReferenceId,
        BalanceAfter = sub.CreditsRemaining,
        CreatedAt = DateTime.UtcNow
    };
}
