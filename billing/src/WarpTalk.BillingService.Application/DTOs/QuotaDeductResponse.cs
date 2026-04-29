namespace WarpTalk.BillingService.Application.DTOs;

/// <summary>
/// Response for quota deduction request
/// </summary>
/// <param name="Success">Whether the deduction was successful</param>
/// <param name="RemainingMinutes">The remaining quota minutes after deduction</param>
/// <param name="Reason">Reason for failure if Success is false</param>
/// <example>
/// {
///   "success": true,
///   "remainingMinutes": 484.5,
///   "reason": null
/// }
/// </example>
public record QuotaDeductResponse(
    bool Success,
    decimal RemainingMinutes,
    string? ErrorCode = null,
    string? Reason = null
);

