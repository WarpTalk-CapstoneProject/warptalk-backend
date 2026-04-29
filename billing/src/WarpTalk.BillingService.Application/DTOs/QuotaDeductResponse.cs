namespace WarpTalk.BillingService.Application.DTOs;

public record QuotaDeductResponse(
    bool Success,
    decimal RemainingMinutes,
    string? Reason = null
);
