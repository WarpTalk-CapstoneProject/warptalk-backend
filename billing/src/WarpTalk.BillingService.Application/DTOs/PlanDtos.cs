using System;

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
    int MaxParticipants,
    string Features,       // JSON blob
    int SortOrder,
    bool IsActive
)
{
    public PlanDto(Guid id, string name, decimal price, int creditsPerMonth, bool isActive, string? features)
        : this(
            id,
            name,
            string.Empty,
            string.Empty,
            price,
            "VND",
            "monthly",
            creditsPerMonth,
            0,
            features ?? "{}",
            0,
            isActive)
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
    bool IsActive = true
);
