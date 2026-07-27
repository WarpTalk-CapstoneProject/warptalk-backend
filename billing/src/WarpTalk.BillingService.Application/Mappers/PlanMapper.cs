using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Application.Mappers;

public static class PlanMapper
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
        plan.OverageCapCredits,
        plan.OveragePricePerCredit,
        plan.LowBalanceThresholdCredits,
        plan.RolloverCapCredits,
        plan.InvoiceTermsDays,
        plan.InvoiceGraceHours,
        plan.MaxParticipants,
        plan.Features,
        plan.SortOrder,
        plan.IsActive,
        plan.MaxLanguages,
        plan.VoiceCloneEnabled,
        plan.AiAssistantEnabled,
        plan.GlossaryEnabled,
        plan.DedicatedGpu
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
        OverageCapCredits = request.OverageCapCredits,
        OveragePricePerCredit = request.OveragePricePerCredit,
        LowBalanceThresholdCredits = request.LowBalanceThresholdCredits,
        RolloverCapCredits = request.RolloverCapCredits,
        InvoiceTermsDays = request.InvoiceTermsDays,
        InvoiceGraceHours = request.InvoiceGraceHours,
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
        plan.OverageCapCredits = request.OverageCapCredits;
        plan.OveragePricePerCredit = request.OveragePricePerCredit;
        plan.LowBalanceThresholdCredits = request.LowBalanceThresholdCredits;
        plan.RolloverCapCredits = request.RolloverCapCredits;
        plan.InvoiceTermsDays = request.InvoiceTermsDays;
        plan.InvoiceGraceHours = request.InvoiceGraceHours;
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
}
