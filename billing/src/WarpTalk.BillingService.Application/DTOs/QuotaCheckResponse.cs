namespace WarpTalk.BillingService.Application.DTOs;

/// <summary>
/// Response for quota check request
/// </summary>
/// <param name="HasQuota">Whether the workspace has remaining minutes</param>
/// <param name="PlanId">Current subscription plan ID</param>
/// <param name="PlanName">Current subscription plan name</param>
/// <param name="RemainingMinutes">Minutes available for use</param>
/// <param name="MaxParticipants">Maximum participants allowed in a session</param>
/// <param name="Features">JSON object of plan features</param>
/// <example>
/// {
///   "hasQuota": true,
///   "planId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
///   "planName": "Pro",
///   "remainingMinutes": 1245.5,
///   "maxParticipants": 50,
///   "features": { "ai_summary": true, "recording": true }
/// }
/// </example>
public record QuotaCheckResponse(
    bool HasQuota,
    string PlanId,
    string PlanName,
    decimal RemainingMinutes,
    int MaxParticipants,
    object Features
);
