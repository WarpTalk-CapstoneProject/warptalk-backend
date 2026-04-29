using System;

namespace WarpTalk.BillingService.Application.DTOs;

public class PaymentLinkResponse
{
    public string CheckoutUrl { get; set; } = string.Empty;
    public long OrderCode { get; set; }
    public string Status { get; set; } = "Pending";
}
