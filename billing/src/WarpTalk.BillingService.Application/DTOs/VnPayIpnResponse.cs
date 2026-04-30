using System.Text.Json.Serialization;

namespace WarpTalk.BillingService.Application.DTOs;

public sealed class VnPayIpnResponse
{
    public VnPayIpnResponse(string rspCode, string message)
    {
        RspCode = rspCode;
        Message = message;
    }

    [JsonPropertyName("RspCode")]
    public string RspCode { get; }

    [JsonPropertyName("Message")]
    public string Message { get; }
}
