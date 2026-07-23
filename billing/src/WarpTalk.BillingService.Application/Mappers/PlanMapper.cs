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
        plan.MaxParticipants,
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
        plan.Features = request.Features;
        plan.SortOrder = request.SortOrder;
        plan.IsActive = request.IsActive;
        plan.UpdatedAt = DateTime.UtcNow;
    }
}
