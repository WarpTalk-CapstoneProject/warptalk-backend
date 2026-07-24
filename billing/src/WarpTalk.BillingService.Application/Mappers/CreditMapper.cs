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

    public static CreditTransaction CreateStripeTopUpTransaction(this Subscription sub, int creditsAdded, Guid userId, Guid referenceId) => new()
    {
        Id = Guid.NewGuid(),
        SubscriptionId = sub.Id,
        UserId = userId,
        Amount = creditsAdded,
        Type = TransactionConstants.TransactionTypes.TopUp,
        Description = BillingMessageConstants.SuccessMessages.StripeCreditTopUp,
        ReferenceId = referenceId,
        ReferenceType = TransactionConstants.ReferenceTypes.StripePayment,
        BalanceAfter = sub.CreditsRemaining,
        CreatedAt = DateTime.UtcNow
    };

    public static CreditTransaction CreateStripeSubscriptionTransaction(this Subscription sub, Plan plan, string paymentType, Guid userId, Guid referenceId) => new()
    {
        Id = Guid.NewGuid(),
        SubscriptionId = sub.Id,
        UserId = userId,
        Amount = plan.CreditsPerCycle,
        Type = TransactionConstants.TransactionTypes.TopUp,
        Description = paymentType == PaymentConstants.PaymentTypes.SubscriptionUpdate 
            ? string.Format(BillingMessageConstants.AdjustmentMessages.PlanUpgradeDirect, plan.Name)
            : string.Format(BillingMessageConstants.SuccessMessages.SubscriptionPlanActivationTemplate, plan.Name),
        ReferenceId = referenceId,
        ReferenceType = TransactionConstants.ReferenceTypes.StripePayment,
        BalanceAfter = sub.CreditsRemaining,
        CreatedAt = DateTime.UtcNow
    };

    public static CreditTransaction CreateRenewalTransaction(this Subscription sub, Plan plan, DateTime newStart) => new()
    {
        Id = Guid.NewGuid(),
        SubscriptionId = sub.Id,
        UserId = sub.UserId,
        WorkspaceId = sub.WorkspaceId,
        Amount = plan.CreditsPerCycle,
        Type = TransactionConstants.TransactionTypes.TopUp,
        Description = string.Format(
            BillingMessageConstants.SuccessMessages.SubscriptionPlanActivationTemplate,
            $"{plan.Name} — Renewal {newStart:yyyy-MM-dd}"),
        ReferenceType = TransactionConstants.ReferenceTypes.Payment,
        ReferenceId = sub.Id,
        BalanceAfter = sub.CreditsRemaining,
        CreatedAt = DateTime.UtcNow
    };

    public static CreditTransaction CreateStaleReservationRefundTransaction(Guid subscriptionId, Guid userId, int amount, int balanceAfter) => new()
    {
        Id = Guid.NewGuid(),
        SubscriptionId = subscriptionId,
        UserId = userId,
        Amount = amount,
        Type = TransactionConstants.TransactionTypes.Refund,
        Description = "Auto-refund for stale reservation",
        ReferenceType = TransactionConstants.ReferenceTypes.CreditReservation,
        BalanceAfter = balanceAfter,
        CreatedAt = DateTime.UtcNow
    };

    public static CreditTransaction CreateAggregatedTransaction(Guid subscriptionId, int amount, string type, string description) => new()
    {
        Id = Guid.NewGuid(),
        SubscriptionId = subscriptionId,
        UserId = Guid.Empty, // Aggregated transactions do not belong to a specific user
        WorkspaceId = Guid.Empty, // Or could pass workspaceId if needed
        Amount = amount,
        Type = type,
        Description = description,
        CreatedAt = DateTime.UtcNow
    };

    public static CreditTransactionDto ToDto(this CreditTransaction t, Guid defaultWorkspaceId = default)
    {
        return new CreditTransactionDto(
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
        );
    }

    public static List<CreditTransactionDto> ToDtoList(this IEnumerable<CreditTransaction> items, Guid defaultWorkspaceId = default)
    {
        return items.Select(t => t.ToDto(defaultWorkspaceId)).ToList();
    }
}
