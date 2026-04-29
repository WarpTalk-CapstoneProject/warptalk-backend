namespace WarpTalk.BillingService.Application.DTOs;

/// <summary>
/// Payload received from PayOS Webhook
/// </summary>
/// <example>
/// {
///   "code": "00",
///   "desc": "success",
///   "data": {
///     "orderCode": 123456,
///     "amount": 200000,
///     "description": "WarpTalk Pro Upgrade",
///     "transactionDateTime": "2026-04-29 10:00:00"
///   },
///   "signature": "38475834759348759348759348"
/// }
/// </example>
public class PayOsWebhookPayload
{
    public string Code { get; set; } = string.Empty;
    public string Desc { get; set; } = string.Empty;
    public PayOsWebhookData? Data { get; set; }
    public string Signature { get; set; } = string.Empty;
}

public class PayOsWebhookData
{
    public long OrderCode { get; set; }
    public int Amount { get; set; }
    public string Description { get; set; } = string.Empty;
    public string AccountNumber { get; set; } = string.Empty;
    public string Reference { get; set; } = string.Empty;
    public string TransactionDateTime { get; set; } = string.Empty;
    public string Currency { get; set; } = "VND";
    public string PaymentLinkId { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string Desc { get; set; } = string.Empty;
}
