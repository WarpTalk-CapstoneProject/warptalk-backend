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

public static class SubscriptionMapper
{
    public static SubscriptionDto ToDto(this Subscription sub, string planName, decimal price) => new(
        sub.Id,
        sub.UserId,
        sub.WorkspaceId,
        sub.PlanId,
        planName,
        price,
        sub.Status.ToLowerInvariant(),
        sub.CreditsRemaining,
        sub.CreditsUsedThisCycle,
        sub.CurrentPeriodStart,
        sub.CurrentPeriodEnd,
        sub.AutoRenew,
        !sub.AutoRenew,
        sub.CreatedAt,
        sub.CancelledAt,
        CreditsPerCycleOverride: sub.CreditsPerCycleOverride,
        ContractPriceVnd: sub.ContractPriceVnd,
        OverageCapCreditsOverride: sub.OverageCapCreditsOverride,
        OveragePricePerCreditOverride: sub.OveragePricePerCreditOverride,
        InvoiceTermsDaysOverride: sub.InvoiceTermsDaysOverride,
        BillingContactEmail: sub.BillingContactEmail,
        OverageCreditsThisCycle: sub.OverageCreditsThisCycle,
        OverageStartedAt: sub.OverageStartedAt,
        ServiceState: sub.ServiceState,
        SuspendedReason: sub.SuspendedReason,
        TrialEndsAt: sub.TrialEndsAt
    );

    public static SubscriptionDto ToDto(this Subscription sub, Plan plan) => new(
        sub.Id,
        sub.UserId,
        sub.WorkspaceId,
        sub.PlanId,
        plan.Name,
        plan.Price,
        sub.Status.ToLowerInvariant(),
        sub.CreditsRemaining,
        sub.CreditsUsedThisCycle,
        sub.CurrentPeriodStart,
        sub.CurrentPeriodEnd,
        sub.AutoRenew,
        !sub.AutoRenew,
        sub.CreatedAt,
        sub.CancelledAt,
        CreditsPerCycleOverride: sub.CreditsPerCycleOverride,
        ContractPriceVnd: sub.ContractPriceVnd,
        OverageCapCreditsOverride: sub.OverageCapCreditsOverride,
        OveragePricePerCreditOverride: sub.OveragePricePerCreditOverride,
        InvoiceTermsDaysOverride: sub.InvoiceTermsDaysOverride,
        BillingContactEmail: sub.BillingContactEmail,
        EffectiveCreditsPerCycle: sub.CreditsPerCycleOverride ?? plan.CreditsPerCycle,
        EffectiveContractPriceVnd: sub.ContractPriceVnd ?? plan.Price,
        EffectiveOverageCapCredits: sub.OverageCapCreditsOverride ?? plan.OverageCapCredits,
        EffectiveOveragePricePerCredit: sub.OveragePricePerCreditOverride ?? plan.OveragePricePerCredit,
        EffectiveInvoiceTermsDays: sub.InvoiceTermsDaysOverride ?? plan.InvoiceTermsDays,
        OverageCreditsThisCycle: sub.OverageCreditsThisCycle,
        OverageStartedAt: sub.OverageStartedAt,
        ServiceState: sub.ServiceState,
        SuspendedReason: sub.SuspendedReason,
        TrialEndsAt: sub.TrialEndsAt
    );

    public static void Cancel(this Subscription sub, string? reason)
    {
        var now = DateTime.UtcNow;
        sub.CancellationReason = reason;
        sub.AutoRenew = false;
        sub.Status = SubscriptionConstants.SubscriptionStatuses.Cancelled;
        sub.UpdatedAt = now;
    }

    public static void CancelImmediately(this Subscription sub, string? reason)
    {
        var now = DateTime.UtcNow;
        sub.Status = SubscriptionConstants.SubscriptionStatuses.Cancelled;
        sub.CancellationReason = reason;
        sub.CancelledAt = now;
        sub.AutoRenew = false;
        sub.IsActive = false;
        sub.UpdatedAt = now;
    }

    /// <summary>
    /// WT-471: the exact inverse of <see cref="Cancel"/>, and only of that one.
    ///
    /// Cancel on a paid subscription flips <c>AutoRenew</c> off and stamps the status; it
    /// deliberately leaves <c>IsActive</c> true and <c>CancelledAt</c> null, because the workspace
    /// keeps everything it paid for until the period ends. So the row is still the live
    /// subscription, and reactivating is a matter of undoing those two fields rather than issuing a
    /// new one.
    ///
    /// It is NOT the inverse of <see cref="CancelImmediately"/>, which runs on the trial path and
    /// does set <c>IsActive = false</c>. Nothing here can bring that back — the caller checks for
    /// it, because a trial that has been ended is not a subscription with renewal switched off.
    ///
    /// <c>CancellationReason</c> is cleared with the rest. Leaving a stale reason on a renewing
    /// subscription would surface a cancellation notice on a plan that is not cancelled.
    /// </summary>
    public static void Reactivate(this Subscription sub)
    {
        sub.AutoRenew = true;
        sub.Status = SubscriptionConstants.SubscriptionStatuses.Active;
        sub.CancellationReason = null;
        sub.CancelledAt = null;
        sub.UpdatedAt = DateTime.UtcNow;
    }

    public static void ResumeAiService(this Subscription sub)
    {
        sub.ServiceState = SubscriptionConstants.ServiceStates.Healthy;
        sub.SuspendedReason = null;
        sub.OverageStartedAt = null;
        sub.UpdatedAt = DateTime.UtcNow;
    }

    public static void ApplyContractTerms(this Subscription sub, UpdateSubscriptionContractTermsRequest request)
    {
        sub.CreditsPerCycleOverride = request.CreditsPerCycleOverride;
        sub.ContractPriceVnd = request.ContractPriceVnd;
        sub.OverageCapCreditsOverride = request.OverageCapCreditsOverride;
        sub.OveragePricePerCreditOverride = request.OveragePricePerCreditOverride;
        sub.InvoiceTermsDaysOverride = request.InvoiceTermsDaysOverride;
        sub.BillingContactEmail = string.IsNullOrWhiteSpace(request.BillingContactEmail)
            ? null
            : request.BillingContactEmail.Trim();
        sub.UpdatedAt = DateTime.UtcNow;
    }

    public static Subscription CreateNewStripeSubscription(Guid workspaceId, Guid userId, Plan plan, DateTime periodEnd)
    {
        var now = DateTime.UtcNow;
        return new Subscription
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            PlanId = plan.Id,
            UserId = userId,
            Status = SubscriptionConstants.SubscriptionStatuses.Active,
            CreditsRemaining = plan.CreditsPerCycle,
            CreditsUsedThisCycle = 0,
            CurrentPeriodStart = now,
            CurrentPeriodEnd = periodEnd,
            AutoRenew = true,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    public static Subscription ToTrialEntity(this TrialSubscriptionRequest request, Plan plan, string ownerDomain)
    {
        var now = DateTime.UtcNow;
        var trialEnd = now.AddDays(SubscriptionConstants.TrialDefaults.DurationDays);

        return new Subscription
        {
            Id = Guid.NewGuid(),
            UserId = request.UserId,
            WorkspaceId = request.WorkspaceId,
            PlanId = plan.Id,
            Status = SubscriptionConstants.SubscriptionStatuses.Active,
            CreditsRemaining = SubscriptionConstants.TrialDefaults.Credits,
            CreditsUsedThisCycle = 0,
            CreditsPerCycleOverride = SubscriptionConstants.TrialDefaults.Credits,
            OverageCapCreditsOverride = SubscriptionConstants.TrialDefaults.OverageCapCredits,
            ContractPriceVnd = null,
            TrialEndsAt = trialEnd,
            OwnerEmailDomain = ownerDomain,
            BillingContactEmail = request.OwnerEmail.Trim(),
            CurrentPeriodStart = now,
            CurrentPeriodEnd = trialEnd,
            AutoRenew = false,
            IsActive = true,
            ServiceState = SubscriptionConstants.ServiceStates.Healthy,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    public static Subscription ToContractSubscriptionEntity(
        this CreateWorkspaceContractSubscriptionRequest request,
        Plan plan)
    {
        var now = DateTime.UtcNow;
        return new Subscription
        {
            Id = Guid.NewGuid(),
            UserId = request.UserId ?? Guid.Empty,
            WorkspaceId = request.WorkspaceId,
            PlanId = request.PlanId,
            Status = SubscriptionConstants.SubscriptionStatuses.Active,
            CreditsRemaining = request.ContractTerms.CreditsPerCycleOverride ?? plan.CreditsPerCycle,
            CreditsPerCycleOverride = request.ContractTerms.CreditsPerCycleOverride,
            ContractPriceVnd = request.ContractTerms.ContractPriceVnd ?? plan.Price,
            OverageCapCreditsOverride = request.ContractTerms.OverageCapCreditsOverride,
            OveragePricePerCreditOverride = request.ContractTerms.OveragePricePerCreditOverride,
            InvoiceTermsDaysOverride = request.ContractTerms.InvoiceTermsDaysOverride,
            BillingContactEmail = request.ContractTerms.BillingContactEmail?.Trim(),
            CreditsUsedThisCycle = 0,
            CurrentPeriodStart = now,
            CurrentPeriodEnd = now.AddDays(30),
            AutoRenew = true,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        };
    }
}
