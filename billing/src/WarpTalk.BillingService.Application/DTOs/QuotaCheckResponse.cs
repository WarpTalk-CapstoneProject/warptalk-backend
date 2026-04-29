namespace WarpTalk.BillingService.Application.DTOs;

public record QuotaCheckResponse(
    bool HasQuota,
    string PlanId,
    string PlanName,
    decimal RemainingMinutes,
    int MaxParticipants,
    object Features
);
