using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.DTOs;

namespace WarpTalk.BillingService.Application.Services;

/// <summary>
/// Real PayOS implementation using HTTP client
/// Calls PayOS API directly instead of SDK
/// </summary>
public class PayOsService : IPayOsService
{
    private readonly string _clientId;
    private readonly string _apiKey;
    private readonly string _checksumKey;
    private readonly string _baseUrl;
    private readonly HttpClient _httpClient;
    private readonly ILogger<PayOsService> _logger;

    public PayOsService(
        IConfiguration configuration,
        ILogger<PayOsService> logger,
        HttpClient httpClient)
    {
        _logger = logger;
        _httpClient = httpClient;

        // Read credentials from configuration
        _clientId = configuration["PayOS:ClientId"] ?? throw new InvalidOperationException("PayOS:ClientId is required");
        _apiKey = configuration["PayOS:ApiKey"] ?? throw new InvalidOperationException("PayOS:ApiKey is required");
        _checksumKey = configuration["PayOS:ChecksumKey"] ?? throw new InvalidOperationException("PayOS:ChecksumKey is required");
        _baseUrl = configuration["PayOS:BaseUrl"] ?? "https://api-sandbox.payos.vn";

        _logger.LogInformation("PayOS Service initialized with base URL: {BaseUrl}", _baseUrl);
    }

    public async Task<PayOsCheckoutResponse> CreateCheckoutLinkAsync(
        long orderCode,
        long amount,
        string description,
        string returnUrl,
        string cancelUrl,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation(
                "Creating PayOS checkout link for OrderCode {OrderCode}, Amount {Amount}",
                orderCode, amount);

            // Build PayOS API request
            var payload = new
            {
                orderCode = orderCode,
                amount = amount,
                description = description,
                items = new[]
                {
                    new
                    {
                        name = description,
                        quantity = 1,
                        price = amount
                    }
                },
                returnUrl = returnUrl,
                cancelUrl = cancelUrl,
                buyerEmail = "customer@example.com",
                buyerName = "Customer",
                buyerPhone = "0123456789"
            };

            var jsonContent = new StringContent(
                JsonSerializer.Serialize(payload),
                System.Text.Encoding.UTF8,
                "application/json");

            // Set authorization header
            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.Add("x-client-id", _clientId);
            _httpClient.DefaultRequestHeaders.Add("x-api-key", _apiKey);

            var response = await _httpClient.PostAsync(
                $"{_baseUrl}/v1/payment-requests",
                jsonContent,
                cancellationToken);

            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var payosResponse = JsonSerializer.Deserialize<PayOsApiResponse>(content);

            if (payosResponse?.Data == null)
            {
                throw new InvalidOperationException("Invalid PayOS response");
            }

            _logger.LogInformation(
                "PayOS checkout link created: {CheckoutUrl}",
                payosResponse.Data.CheckoutUrl);

            return new PayOsCheckoutResponse
            {
                Code = "00",
                Desc = "Success",
                Data = new PayOsCheckoutData
                {
                    CheckoutUrl = payosResponse.Data.CheckoutUrl,
                    QrCode = payosResponse.Data.QrCode ?? string.Empty
                }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create PayOS checkout link for OrderCode {OrderCode}", orderCode);
            throw;
        }
    }

    public async Task<PayOsOrderDetailsResponse> GetCheckoutLinkDetailsAsync(
        long orderCode,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Fetching PayOS order details for OrderCode {OrderCode}", orderCode);

            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.Add("x-client-id", _clientId);
            _httpClient.DefaultRequestHeaders.Add("x-api-key", _apiKey);

            var response = await _httpClient.GetAsync(
                $"{_baseUrl}/v1/payment-requests/{orderCode}",
                cancellationToken);

            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var payosResponse = JsonSerializer.Deserialize<PayOsOrderResponse>(content);

            if (payosResponse?.Data == null)
            {
                throw new InvalidOperationException("Invalid PayOS response");
            }

            var order = payosResponse.Data;

            _logger.LogInformation(
                "PayOS order retrieved: Status {Status}, AmountPaid {AmountPaid}",
                order.Status, order.AmountPaid);

            return new PayOsOrderDetailsResponse
            {
                Code = "00",
                Desc = "Success",
                Data = new PayOsOrderData
                {
                    OrderCode = order.OrderCode,
                    Amount = order.Amount,
                    AmountPaid = order.AmountPaid,
                    AmountRemaining = order.AmountRemaining,
                    Status = order.Status,
                    CreatedAt = UnixTimeStampToDateTime(order.CreatedAt),
                    ExpiredAt = UnixTimeStampToDateTime(order.ExpiredAt),
                    CanceledAt = order.CanceledAt?.ToString() ?? string.Empty,
                    Description = order.Description,
                    CheckoutUrl = order.CheckoutUrl,
                    ReturnUrl = order.ReturnUrl,
                    CancelUrl = order.CancelUrl,
                    Reference = order.Reference ?? string.Empty,
                    Transactions = MapTransactions(order.Transactions)
                }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get PayOS order details for OrderCode {OrderCode}", orderCode);
            throw;
        }
    }

    public async Task<PayOsCancelResponse> CancelCheckoutLinkAsync(
        long orderCode,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Canceling PayOS checkout link for OrderCode {OrderCode}", orderCode);

            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.Add("x-client-id", _clientId);
            _httpClient.DefaultRequestHeaders.Add("x-api-key", _apiKey);

            var response = await _httpClient.DeleteAsync(
                $"{_baseUrl}/v1/payment-requests/{orderCode}",
                cancellationToken);

            response.EnsureSuccessStatusCode();

            _logger.LogInformation(
                "PayOS checkout link canceled for OrderCode {OrderCode}",
                orderCode);

            return new PayOsCancelResponse
            {
                Code = "00",
                Desc = "Success",
                Data = new { }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cancel PayOS checkout link for OrderCode {OrderCode}", orderCode);
            throw;
        }
    }

    public async Task<PayOsRefundResponse> RefundAsync(
        string orderCode,
        long amount,
        string reason,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!long.TryParse(orderCode, out var parsedOrderCode))
            {
                throw new ArgumentException("Invalid order code format", nameof(orderCode));
            }

            _logger.LogInformation(
                "Processing PayOS refund for OrderCode {OrderCode}, Amount {Amount}",
                orderCode, amount);

            var payload = new
            {
                orderCode = parsedOrderCode,
                amount = amount,
                description = reason
            };

            var jsonContent = new StringContent(
                JsonSerializer.Serialize(payload),
                System.Text.Encoding.UTF8,
                "application/json");

            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.Add("x-client-id", _clientId);
            _httpClient.DefaultRequestHeaders.Add("x-api-key", _apiKey);

            var response = await _httpClient.PostAsync(
                $"{_baseUrl}/v1/payment-requests/{parsedOrderCode}/refunds",
                jsonContent,
                cancellationToken);

            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var payosResponse = JsonSerializer.Deserialize<PayOsRefundApiResponse>(content);

            if (payosResponse?.Data == null)
            {
                throw new InvalidOperationException("Invalid PayOS refund response");
            }

            var refund = payosResponse.Data;

            _logger.LogInformation(
                "PayOS refund processed: Status {Status}, Amount {Amount}",
                refund.Status, refund.Amount);

            return new PayOsRefundResponse
            {
                Code = "00",
                Desc = "Success",
                Data = new PayOsRefundData
                {
                    RefundId = refund.Id,
                    OrderCode = refund.OrderCode.ToString(),
                    RefundAmount = refund.Amount,
                    Status = refund.Status,
                    CreatedAt = UnixTimeStampToDateTime(refund.CreatedAt)
                }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process PayOS refund for OrderCode {OrderCode}", orderCode);
            throw;
        }
    }

    private static DateTime UnixTimeStampToDateTime(long unixTimeStamp)
    {
        var dateTime = new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc);
        dateTime = dateTime.AddSeconds(unixTimeStamp).ToUniversalTime();
        return dateTime;
    }

    private static PayOsTransactionData[] MapTransactions(List<PayOsTransactionApiModel> transactions)
    {
        if (transactions == null || transactions.Count == 0)
        {
            return Array.Empty<PayOsTransactionData>();
        }

        var result = new PayOsTransactionData[transactions.Count];
        for (int i = 0; i < transactions.Count; i++)
        {
            result[i] = new PayOsTransactionData
            {
                Reference = transactions[i].Reference ?? string.Empty,
                Amount = transactions[i].Amount,
                Status = transactions[i].Status,
                CreatedAt = UnixTimeStampToDateTime(transactions[i].CreatedAt),
                AccountNumber = transactions[i].AccountNumber ?? string.Empty,
                AccountName = transactions[i].AccountName ?? string.Empty,
                Method = transactions[i].Method ?? string.Empty
            };
        }
        return result;
    }
}

// Internal API Models for PayOS responses
internal class PayOsApiResponse
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("data")]
    public PayOsCheckoutApiModel Data { get; set; } = new();
}

internal class PayOsCheckoutApiModel
{
    [JsonPropertyName("checkoutUrl")]
    public string CheckoutUrl { get; set; } = string.Empty;

    [JsonPropertyName("qrCode")]
    public string? QrCode { get; set; }
}

internal class PayOsOrderResponse
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("data")]
    public PayOsOrderApiModel Data { get; set; } = new();
}

internal class PayOsOrderApiModel
{
    [JsonPropertyName("orderCode")]
    public long OrderCode { get; set; }

    [JsonPropertyName("amount")]
    public long Amount { get; set; }

    [JsonPropertyName("amountPaid")]
    public long AmountPaid { get; set; }

    [JsonPropertyName("amountRemaining")]
    public long AmountRemaining { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("createdAt")]
    public long CreatedAt { get; set; }

    [JsonPropertyName("expiredAt")]
    public long ExpiredAt { get; set; }

    [JsonPropertyName("canceledAt")]
    public long? CanceledAt { get; set; }

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("checkoutUrl")]
    public string CheckoutUrl { get; set; } = string.Empty;

    [JsonPropertyName("returnUrl")]
    public string ReturnUrl { get; set; } = string.Empty;

    [JsonPropertyName("cancelUrl")]
    public string CancelUrl { get; set; } = string.Empty;

    [JsonPropertyName("reference")]
    public string? Reference { get; set; }

    [JsonPropertyName("transactions")]
    public List<PayOsTransactionApiModel> Transactions { get; set; } = new();
}

internal class PayOsTransactionApiModel
{
    [JsonPropertyName("reference")]
    public string? Reference { get; set; }

    [JsonPropertyName("amount")]
    public long Amount { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("createdAt")]
    public long CreatedAt { get; set; }

    [JsonPropertyName("accountNumber")]
    public string? AccountNumber { get; set; }

    [JsonPropertyName("accountName")]
    public string? AccountName { get; set; }

    [JsonPropertyName("method")]
    public string? Method { get; set; }
}

internal class PayOsRefundApiResponse
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("data")]
    public PayOsRefundApiModel Data { get; set; } = new();
}

internal class PayOsRefundApiModel
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("orderCode")]
    public long OrderCode { get; set; }

    [JsonPropertyName("amount")]
    public long Amount { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("createdAt")]
    public long CreatedAt { get; set; }
}

