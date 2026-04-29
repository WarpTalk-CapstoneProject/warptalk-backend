using System.ComponentModel.DataAnnotations;

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
    [Required]
    [StringLength(10)]
    public string Code { get; set; } = string.Empty;

    [Required]
    [StringLength(255)]
    public string Desc { get; set; } = string.Empty;

    [Required]
    public PayOsWebhookData? Data { get; set; }

    [Required]
    [StringLength(512)]
    public string Signature { get; set; } = string.Empty;
}

public class PayOsWebhookData
{
    [Range(1, long.MaxValue)]
    public long OrderCode { get; set; }

    [Range(1, int.MaxValue)]
    public int Amount { get; set; }

    [StringLength(255)]
    public string Description { get; set; } = string.Empty;

    [StringLength(100)]
    public string AccountNumber { get; set; } = string.Empty;

    [Required]
    [StringLength(128)]
    public string Reference { get; set; } = string.Empty;

    [Required]
    [StringLength(64)]
    public string TransactionDateTime { get; set; } = string.Empty;

    [Required]
    [RegularExpression("^[A-Z]{3}$")]
    public string Currency { get; set; } = "VND";

    [StringLength(128)]
    public string PaymentLinkId { get; set; } = string.Empty;

    [StringLength(10)]
    public string Code { get; set; } = string.Empty;

    [StringLength(255)]
    public string Desc { get; set; } = string.Empty;
}
