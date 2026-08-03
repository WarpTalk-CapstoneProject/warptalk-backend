using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.Json;
using System;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Helpers;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.Shared.Models;

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







    public static CreditTransaction CreateStripeSubscriptionTransaction(StripeSubscriptionTransactionRequest request) => new()
    {
        Id = Guid.NewGuid(),
        SubscriptionId = request.Subscription.Id,
        UserId = request.UserId,
        Amount = request.Plan.CreditsPerCycle,
        Type = TransactionConstants.TransactionTypes.TopUp,
        Description = request.PaymentType == PaymentConstants.PaymentTypes.SubscriptionUpdate
            ? string.Format(BillingMessageConstants.AdjustmentMessages.PlanUpgradeDirect, request.Plan.Name)
            : string.Format(BillingMessageConstants.SuccessMessages.SubscriptionPlanActivationTemplate, request.Plan.Name),
        ReferenceId = request.ReferenceId,
        ReferenceType = TransactionConstants.ReferenceTypes.StripePayment,
        BalanceAfter = request.Subscription.CreditsRemaining,
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
            string.Format(BillingMessageConstants.SuccessMessages.SubscriptionPlanRenewalTemplate, plan.Name, newStart)),
        ReferenceType = TransactionConstants.ReferenceTypes.Payment,
        ReferenceId = sub.Id,
        BalanceAfter = sub.CreditsRemaining,
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

    public static CreditTransactionDto ToCreditTransactionDto(
        this SettleUsageChargeResult settlement,
        ConsumeCreditsRequest request,
        Subscription subscription,
        Guid workspaceId)
    {
        var settledTransaction = new CreditTransaction
        {
            Id = settlement.TransactionId ?? Guid.NewGuid(),
            SubscriptionId = subscription.Id,
            UserId = subscription.UserId,
            WorkspaceId = workspaceId,
            Amount = -request.Amount,
            Type = TransactionConstants.TransactionTypes.Consume,
            ReferenceType = request.ReferenceType,
            ReferenceId = request.ReferenceId,
            BalanceAfter = settlement.BalanceAfter ?? subscription.CreditsRemaining,
            CreatedAt = DateTime.UtcNow
        };

        return settledTransaction.ToDto();
    }

    public static List<CreditTransactionDto> ToDtoList(this IEnumerable<CreditTransaction> items, Guid defaultWorkspaceId = default)
    {
        return items.Select(t => t.ToDto(defaultWorkspaceId)).ToList();
    }

}
