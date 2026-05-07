namespace WarpTalk.BillingService.Application.DTOs;

/// <summary>
/// DTOs for simulating PayOS payment provider integration.
/// Based on PayOS official documentation structure.
/// </summary>

public record PayOSWebhookRequest(
    string code,
    string desc,
    PayOSData data,
    string signature
);

public record PayOSData(
    long orderCode,
    int amount,
    string description,
    string status,
    string checkoutResponseCode,
    string paymentLinkId
);

public record PayOSCreateLinkRequest(
    long orderCode,
    int amount,
    string description,
    string cancelUrl,
    string returnUrl
);

public record PayOSCreateLinkResponse(
    string bin,
    string accountNumber,
    string accountName,
    int amount,
    string description,
    long orderCode,
    string paymentLinkId,
    string status,
    string checkoutUrl,
    string qrCode
);
