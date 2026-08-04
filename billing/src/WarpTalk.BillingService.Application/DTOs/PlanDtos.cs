using System;
using WarpTalk.BillingService.Domain.Constants;

namespace WarpTalk.BillingService.Application.DTOs;


public record PlanDto(
    Guid Id,
    string Name,
    string Slug,
    string Tier,
    decimal Price,
    string Currency,
    string BillingCycle,
    int CreditsPerCycle,
    int OverageCapCredits,
    decimal OveragePricePerCredit,
    int LowBalanceThresholdCredits,
    int RolloverCapCredits,
    int InvoiceTermsDays,
    int InvoiceGraceHours,
    int MaxParticipants,
    string Features,       // JSON blob
    int SortOrder,
    bool IsActive,
    int MaxLanguages = SubscriptionConstants.PlanDefaults.MaxLanguages,
    bool VoiceCloneEnabled = false,
    bool AiAssistantEnabled = false,
    bool GlossaryEnabled = false,
    bool DedicatedGpu = false
)
{
    public PlanDto(Guid id, string name, decimal price, int creditsPerMonth, bool isActive, string? features)
        : this(
            id,
            name,
            string.Empty,
            string.Empty,
            price,
            PaymentConstants.Currencies.Usd,
            SubscriptionConstants.BillingCycles.Monthly,
            creditsPerMonth,
            0,
            SubscriptionConstants.PlanDefaults.OveragePricePerCredit,
            0,
            0,
            SubscriptionConstants.PlanDefaults.InvoiceTermsDays,
            SubscriptionConstants.PlanDefaults.InvoiceGraceHours,
            0,
            features ?? SubscriptionConstants.FeatureAccess.EmptyFeaturesJson,
            0,
            isActive,
            SubscriptionConstants.PlanDefaults.MaxLanguages,
            false,
            false,
            false,
            false)
    {
    }
}

public record PlanRequest(
    string Name,
    string Slug,
    string Tier,
    decimal Price,
    string Currency,
    string BillingCycle,
    int CreditsPerCycle,
    int MaxParticipants,
    string Features,
    int SortOrder,
    int OverageCapCredits = 0,
    decimal OveragePricePerCredit = SubscriptionConstants.PlanDefaults.OveragePricePerCredit,
    int LowBalanceThresholdCredits = 0,
    int RolloverCapCredits = 0,
    int InvoiceTermsDays = SubscriptionConstants.PlanDefaults.InvoiceTermsDays,
    int InvoiceGraceHours = SubscriptionConstants.PlanDefaults.InvoiceGraceHours,
    bool IsActive = true,
    int MaxLanguages = SubscriptionConstants.PlanDefaults.MaxLanguages,
    bool VoiceCloneEnabled = false,
    bool AiAssistantEnabled = false,
    bool GlossaryEnabled = false,
    bool DedicatedGpu = false
);
