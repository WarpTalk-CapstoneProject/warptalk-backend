using System;

namespace WarpTalk.BillingService.Application.DTOs;

/// <summary>
/// Response from PayOS create checkout link
/// </summary>
public class PayOsCheckoutResponse
{
    public string Code { get; set; } = string.Empty;
    public string Desc { get; set; } = string.Empty;
    public PayOsCheckoutData Data { get; set; } = new();
}

/// <summary>
/// PayOS checkout link data
/// </summary>
public class PayOsCheckoutData
{
    public string CheckoutUrl { get; set; } = string.Empty;
    public string QrCode { get; set; } = string.Empty;
}

/// <summary>
/// Response from PayOS get order details
/// </summary>
public class PayOsOrderDetailsResponse
{
    public string Code { get; set; } = string.Empty;
    public string Desc { get; set; } = string.Empty;
    public PayOsOrderData Data { get; set; } = new();
}

/// <summary>
/// PayOS order details
/// </summary>
public class PayOsOrderData
{
    public long OrderCode { get; set; }
    public long Amount { get; set; }
    public long AmountPaid { get; set; }
    public long AmountRemaining { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiredAt { get; set; }
    public string CanceledAt { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string CheckoutUrl { get; set; } = string.Empty;
    public string ReturnUrl { get; set; } = string.Empty;
    public string CancelUrl { get; set; } = string.Empty;
    public string Reference { get; set; } = string.Empty;
    public PayOsTransactionData[] Transactions { get; set; } = [];
}

/// <summary>
/// PayOS transaction data
/// </summary>
public class PayOsTransactionData
{
    public string Reference { get; set; } = string.Empty;
    public long Amount { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public string AccountNumber { get; set; } = string.Empty;
    public string AccountName { get; set; } = string.Empty;
    public string Method { get; set; } = string.Empty;
}

/// <summary>
/// Response from PayOS cancel checkout
/// </summary>
public class PayOsCancelResponse
{
    public string Code { get; set; } = string.Empty;
    public string Desc { get; set; } = string.Empty;
    public object Data { get; set; } = new();
}

/// <summary>
/// Response from PayOS refund
/// </summary>
public class PayOsRefundResponse
{
    public string Code { get; set; } = string.Empty;
    public string Desc { get; set; } = string.Empty;
    public PayOsRefundData Data { get; set; } = new();
}

/// <summary>
/// PayOS refund data
/// </summary>
public class PayOsRefundData
{
    public string RefundId { get; set; } = string.Empty;
    public string OrderCode { get; set; } = string.Empty;
    public long RefundAmount { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}
