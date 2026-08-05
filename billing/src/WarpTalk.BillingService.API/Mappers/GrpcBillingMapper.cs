using System;
using System.Linq;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.Shared;
using Protos = WarpTalk.Shared.Protos;

namespace WarpTalk.BillingService.API.Mappers;

internal static class GrpcBillingMapper
{
    public static Protos.CreditTransaction ToGrpc(this CreditTransactionDto dto)
    {
        return new Protos.CreditTransaction
        {
            Id = dto.Id.ToString(),
            Amount = dto.Amount,
            Type = dto.Type,
            Description = dto.Description ?? string.Empty,
            ReferenceType = dto.ReferenceType ?? string.Empty,
            ReferenceId = dto.ReferenceId?.ToString() ?? string.Empty,
            BalanceAfter = dto.BalanceAfter,
            CreatedAt = dto.CreatedAt.ToString("o")
        };
    }

    public static Protos.GetCreditsResponse ToGrpc(this CreditBalanceDto dto)
    {
        return new Protos.GetCreditsResponse
        {
            WorkspaceId = dto.WorkspaceId.ToString(),
            CurrentCredits = dto.CurrentCredits,
            Status = dto.Status
        };
    }

    public static Protos.ConsumeCreditsResponse ToConsumeCreditsResponse(this CreditTransactionDto dto)
    {
        return new Protos.ConsumeCreditsResponse
        {
            Success = true,
            NewBalance = dto.BalanceAfter,
            ErrorMessage = string.Empty
        };
    }

    public static Protos.CreditHistoryResponse ToGrpc(this PaginatedResponse<CreditTransactionDto> dto)
    {
        var response = new Protos.CreditHistoryResponse { TotalCount = dto.TotalCount };
        response.Items.AddRange(dto.Items.Select(x => x.ToGrpc()));
        return response;
    }

    public static ConsumeCreditsRequest ToDto(this Protos.ConsumeCreditsRequest request, Guid workspaceId)
    {
        Guid? referenceId = null;
        if (!string.IsNullOrEmpty(request.ReferenceId) && Guid.TryParse(request.ReferenceId, out var parsedRefId))
            referenceId = parsedRefId;

        return new ConsumeCreditsRequest(workspaceId, request.Amount, request.ReferenceType, referenceId);
    }



    public static CreditHistoryQuery ToCreditHistoryQuery(this Protos.GetHistoryRequest request)
    {
        return new CreditHistoryQuery
        {
            PageNumber = request.PageNumber > 0 ? request.PageNumber : 1,
            PageSize = request.PageSize > 0 ? request.PageSize : 50
        };
    }

    public static Protos.PaymentTransaction ToGrpc(this PaymentTransactionDto dto)
    {
        return new Protos.PaymentTransaction
        {
            Id = dto.Id.ToString(),
            Amount = (double)dto.Amount,
            Status = dto.Status,
            CreatedAt = dto.CreatedAt.ToString("o")
        };
    }

    public static Protos.TransactionHistoryResponse ToGrpc(this PaginatedResponse<PaymentTransactionDto> dto)
    {
        var response = new Protos.TransactionHistoryResponse { TotalCount = dto.TotalCount };
        response.Items.AddRange(dto.Items.Select(x => x.ToGrpc()));
        return response;
    }

    public static PaginationQuery ToPaginationQuery(this Protos.GetHistoryRequest request)
    {
        return new PaginationQuery(
            request.PageNumber > 0 ? request.PageNumber : 1,
            request.PageSize > 0 ? request.PageSize : 50);
    }

    public static StripePaymentEventRequest ToDto(this Protos.ProcessPaymentEventRequest request)
    {
        return new StripePaymentEventRequest(
            StripeSessionId: request.StripeSessionId,
            PaymentIntentId: request.ProviderTransactionId,
            Amount: (decimal)request.Amount,
            Currency: request.Currency,
            UserIdStr: request.UserId,
            WorkspaceIdStr: request.WorkspaceId,
            PaymentType: request.PaymentType,
            Status: request.Status,
            FailureReason: request.FailureReason,
            InvoiceUrl: request.InvoiceUrl,
            InvoicePdf: request.InvoicePdf,
            PlanSlug: request.PlanSlug,
            BillingCycle: request.BillingCycle);
    }

    public static Protos.ProcessPaymentResponse ToProcessPaymentResponse(this Result result)
    {
        return result.IsSuccess
            ? new Protos.ProcessPaymentResponse { Success = true, ErrorMessage = string.Empty }
            : new Protos.ProcessPaymentResponse { Success = false, ErrorMessage = result.Error };
    }

    public static Protos.SubscriptionResponse ToGrpc(this Subscription sub, string planName)
    {
        return new Protos.SubscriptionResponse
        {
            SubscriptionId = sub.Id.ToString(),
            Status = sub.Status.ToString().ToLowerInvariant(),
            ErrorMessage = string.Empty,
            PlanId = sub.PlanId.ToString(),
            PlanName = planName,
            WorkspaceId = sub.WorkspaceId.ToString(),
            CreditsRemaining = sub.CreditsRemaining,
            CurrentPeriodStart = sub.CurrentPeriodStart.ToString("o"),
            CurrentPeriodEnd = sub.CurrentPeriodEnd.ToString("o"),
            AutoRenew = sub.AutoRenew,
            CancelledAt = sub.CancelledAt?.ToString("o") ?? string.Empty
        };
    }

    public static Protos.SubscriptionResponse ToGrpc(this SubscriptionDto dto)
    {
        return new Protos.SubscriptionResponse
        {
            SubscriptionId = dto.Id.ToString(),
            Status = dto.Status.ToLowerInvariant(),
            ErrorMessage = string.Empty,
            PlanId = dto.PlanId.ToString(),
            PlanName = dto.PlanName,
            WorkspaceId = dto.WorkspaceId.ToString(),
            CreditsRemaining = dto.CreditsRemaining,
            CurrentPeriodStart = dto.CurrentPeriodStart.ToString("o"),
            CurrentPeriodEnd = dto.CurrentPeriodEnd.ToString("o"),
            AutoRenew = dto.AutoRenew,
            CancelledAt = dto.CancelledAt?.ToString("o") ?? string.Empty
        };
    }

    public static Protos.SubscriptionResponse ToEmptySubscriptionResponse(string errorMessage)
    {
        return new Protos.SubscriptionResponse
        {
            Status = SubscriptionConstants.SubscriptionStatuses.None,
            ErrorMessage = errorMessage
        };
    }

    public static Protos.SubscriptionResponse ToCancelledSubscriptionResponse()
    {
        return new Protos.SubscriptionResponse
        {
            Status = SubscriptionConstants.SubscriptionStatuses.Cancelled,
            ErrorMessage = string.Empty
        };
    }

    public static Protos.GetFeatureAccessResponse ToEmptyFeatureAccessResponse()
    {
        return new Protos.GetFeatureAccessResponse
        {
            HasActiveSubscription = false,
            PlanTier = SubscriptionConstants.Tiers.NoActivePlan
        };
    }

    /// <summary>
    /// Projects a subscription's plan onto the feature-access contract. Every quota and feature
    /// field reads its own column on <see cref="Plan"/> — WT-262: these used to be hardcoded to
    /// <c>true</c>, or derived from a <c>Tier</c> string comparison, which meant the columns on
    /// <c>subscription.plans</c> were never actually consulted by anything.
    ///
    /// When <paramref name="plan"/> is null the plan row behind this subscription could not be
    /// resolved, so no entitlement can be stated. That case falls back to
    /// <see cref="SubscriptionConstants.PlanDefaults"/> — the documented contract minimums — and
    /// to every feature OFF, rather than to the previous "grant everything". Fabricating
    /// <c>true</c> here handed paid features to a workspace whose plan we failed to read, which is
    /// the strictly worse failure of the two: an under-grant is visible and recoverable, an
    /// over-grant is silent and unbilled. Callers are expected to key off
    /// <c>has_active_subscription</c> before treating these numbers as a limit.
    /// </summary>
    public static Protos.GetFeatureAccessResponse ToFeatureAccessResponse(this Subscription sub, Plan? plan)
    {
        bool hasActiveSubscription = sub.IsActive &&
                                     sub.Status == SubscriptionConstants.SubscriptionStatuses.Active &&
                                     sub.CurrentPeriodEnd >= DateTime.UtcNow;

        return new Protos.GetFeatureAccessResponse
        {
            HasActiveSubscription = hasActiveSubscription,
            PlanTier = plan?.Tier ?? SubscriptionConstants.Tiers.NoActivePlan,
            MaxParticipants = plan?.MaxParticipants ?? SubscriptionConstants.PlanDefaults.MaxParticipants,
            MaxLanguages = plan?.MaxLanguages ?? SubscriptionConstants.PlanDefaults.MaxLanguages,
            VoiceCloneEnabled = plan?.VoiceCloneEnabled ?? false,
            AiAssistantEnabled = plan?.AiAssistantEnabled ?? false,
            GlossaryEnabled = plan?.GlossaryEnabled ?? false,
            DedicatedGpu = plan?.DedicatedGpu ?? false,
            FeaturesJson = plan?.Features ?? SubscriptionConstants.FeatureAccess.EmptyFeaturesJson,
            AllowGlossary = plan?.GlossaryEnabled ?? false

            // WT-263: allow_acl is gone. It had no backing column and was mirrored from
            // ai_assistant_enabled as an admitted stand-in; the product decision was to drop the
            // field rather than add a column, so it is not carried into the entitlement map either.
            // Field number 11 is reserved in billing.proto.
        };
    }
}
