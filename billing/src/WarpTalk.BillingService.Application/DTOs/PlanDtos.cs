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
    bool AllowGlossary,
    bool AllowAcl,
    string Features,       // JSON blob
    int SortOrder
);

public record CreatePlanRequest(
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
    int VoiceCloneLimitMins,
    bool AllowGlossary,
    bool AllowAcl,
    string Features,
    int SortOrder
);

public record UpdatePlanRequest(
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
    int VoiceCloneLimitMins,
    bool AllowGlossary,
    bool AllowAcl,
    string Features,
    int SortOrder,
    bool IsActive
);
