namespace WarpTalk.BillingService.Application.DTOs;

public class PayOsWebhookProcessResult
{
    public bool Success { get; set; }
    public string ResultCode { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public long OrderCode { get; set; }
    public string TransactionStatus { get; set; } = string.Empty;
}
