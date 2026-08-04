namespace WarpTalk.BillingService.Application.DTOs;

public record BillingPolicyDto(
    decimal VatRate);

public record UpdateBillingPolicyRequest(
    decimal VatRate);
