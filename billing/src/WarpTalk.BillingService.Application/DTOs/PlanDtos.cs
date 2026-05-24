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
    int MaxLanguages,
    bool VoiceCloneEnabled,
    bool AiAssistantEnabled,
    bool GlossaryEnabled,
    bool DedicatedGpu,
    string Features,       // JSON blob
    int SortOrder
);
