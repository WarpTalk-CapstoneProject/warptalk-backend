using System;
using System.Collections.Generic;
using System.Linq;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Application.Mappers;

public static class CreditMapper
{
    public static CreditBalanceDto ToCreditBalanceDto(this Subscription sub, Guid workspaceId) => new(
        workspaceId,
        sub.CreditsRemaining,
        sub.CreditsUsedThisCycle,
        sub.CreditsRemaining + sub.CreditsUsedThisCycle,
        sub.Status.ToString().ToLower(),
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
        Type = TransactionConstants.TransactionTypes.Consume,
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
        Type = TransactionConstants.TransactionTypes.TopUp,
        ReferenceType = request.ReferenceType.ToString(),
        ReferenceId = request.ReferenceId,
        BalanceAfter = sub.CreditsRemaining,
        CreatedAt = DateTime.UtcNow
    };

    public static CreditTransaction ToEntity(this ManualAdjustCreditsRequest request, Subscription sub) => new()
    {
        Id = Guid.NewGuid(),
        SubscriptionId = sub.Id,
        UserId = sub.UserId,
        Amount = request.Amount,
        Type = TransactionConstants.TransactionTypes.Adjustment,
        Description = string.IsNullOrEmpty(request.Reason) ? BillingMessageConstants.AdjustmentMessages.DefaultReason : request.Reason,
        ReferenceType = TransactionConstants.ReferenceTypes.ManualAdjustment,
        ReferenceId = null,
        BalanceAfter = sub.CreditsRemaining,
        CreatedAt = DateTime.UtcNow
    };

    public static List<CreditTransactionDto> ToDtoList(this IEnumerable<CreditTransaction> items, Guid defaultWorkspaceId = default)
    {
        return items.Select(t => new CreditTransactionDto(
            t.Id,
            t.Amount,
            t.Type,
            t.Description,
            t.ReferenceType,
            t.ReferenceId,
            t.BalanceAfter,
            t.CreatedAt,
            t.Subscription?.WorkspaceId ?? defaultWorkspaceId,
            null,
            t.UserId,
            null
        )).ToList();
    }
}
