# PayOS Service API Reference

## Overview

`IPayOsService` is the main abstraction for interacting with PayOS payment gateway. It provides 4 core operations for managing payment checkouts and refunds.

---

## Interface Definition

```csharp
public interface IPayOsService
{
    /// <summary>
    /// Creates a PayOS checkout link for a payment order
    /// </summary>
    /// <param name="orderCode">Unique order identifier (must be unique within 24 hours)</param>
    /// <param name="amount">Amount in VND (Vietnamese Dong)</param>
    /// <param name="description">Payment description for customer (50 chars max)</param>
    /// <param name="returnUrl">URL to redirect after successful payment</param>
    /// <param name="cancelUrl">URL to redirect if payment is cancelled</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>PayOsCheckoutResponse with checkoutUrl and qrCode</returns>
    Task<PayOsCheckoutResponse> CreateCheckoutLinkAsync(
        long orderCode, 
        long amount, 
        string description, 
        string returnUrl, 
        string cancelUrl, 
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves detailed status and payment information for an order
    /// </summary>
    /// <param name="orderCode">Order identifier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>PayOsOrderDetailsResponse with full order details</returns>
    Task<PayOsOrderDetailsResponse> GetCheckoutLinkDetailsAsync(
        long orderCode, 
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels an unpaid checkout link
    /// </summary>
    /// <param name="orderCode">Order identifier to cancel</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>PayOsCancelResponse with cancellation status</returns>
    Task<PayOsCancelResponse> CancelCheckoutLinkAsync(
        long orderCode, 
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Issues a refund for a paid order
    /// </summary>
    /// <param name="orderCode">Order identifier (stringified for API)</param>
    /// <param name="amount">Refund amount in VND</param>
    /// <param name="reason">Reason for refund (for accounting)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>PayOsRefundResponse with refund details</returns>
    Task<PayOsRefundResponse> RefundAsync(
        string orderCode, 
        long amount, 
        string reason, 
        CancellationToken cancellationToken = default);
}
```

---

## Methods

### 1. CreateCheckoutLinkAsync

**Purpose**: Generate a PayOS checkout link for a new order

**Request Parameters**:
```csharp
long orderCode = 123456;           // Unique order ID, must be unique within 24 hours
long amount = 199000;              // Amount in VND (199,000 VND = ~$8 USD)
string description = "Pro Plan Upgrade";  // Max 50 characters
string returnUrl = "https://app.warptalk.vn/checkout/success";  // Success redirect
string cancelUrl = "https://app.warptalk.vn/checkout/cancel";   // Cancel redirect
```

**Response**:
```csharp
public class PayOsCheckoutResponse
{
    public string Code { get; set; }           // "00" = success
    public string Desc { get; set; }           // "Success" or error message
    public PayOsCheckoutData Data { get; set; }  // See below
}

public class PayOsCheckoutData
{
    public string CheckoutUrl { get; set; }    // https://pay.payos.vn/web/...
    public string QrCode { get; set; }         // QR code image (PNG base64 or URL)
}
```

**Example**:
```csharp
var response = await _payOsService.CreateCheckoutLinkAsync(
    orderCode: 2603300123456,
    amount: 199000,
    description: "Pro Plan Upgrade",
    returnUrl: "https://my-domain.com/success",
    cancelUrl: "https://my-domain.com/cancel"
);

if (response.Code == "00")
{
    // Success - return checkoutUrl to frontend
    return Ok(new { checkoutUrl = response.Data.CheckoutUrl });
}
else
{
    // Error
    _logger.LogError($"PayOS error: {response.Desc}");
    throw new PaymentException(response.Desc);
}
```

**Status Codes**:
- `00` - Success
- `01` - Invalid request
- `02` - Authentication failed
- `03` - Order already exists
- `04` - Amount invalid
- `50` - Internal server error

---

### 2. GetCheckoutLinkDetailsAsync

**Purpose**: Check payment status and full order details

**Request Parameters**:
```csharp
long orderCode = 123456;  // Order to check
```

**Response**:
```csharp
public class PayOsOrderDetailsResponse
{
    public string Code { get; set; }              // "00" = success
    public string Desc { get; set; }              // Status message
    public PayOsOrderData OrderData { get; set; } // See below
}

public class PayOsOrderData
{
    public long OrderCode { get; set; }          // Order ID
    public long Amount { get; set; }             // Total amount in VND
    public long AmountPaid { get; set; }         // Amount actually paid
    public long AmountRemaining { get; set; }    // Amount still due
    public string Status { get; set; }           // "PENDING", "PROCESSING", "COMPLETED", "CANCELLED"
    public DateTime CreatedAt { get; set; }      // Order creation time (UTC)
    public DateTime ExpiredAt { get; set; }      // Link expiration time (UTC, 15 min default)
    public List<PayOsTransactionData> Transactions { get; set; } // Payment transactions
}

public class PayOsTransactionData
{
    public string Reference { get; set; }        // Payment reference ID
    public long Amount { get; set; }             // Transaction amount
    public string Status { get; set; }           // "SUCCESS", "FAILED", "PENDING"
    public DateTime CreatedAt { get; set; }      // Transaction timestamp (UTC)
    public string AccountNumber { get; set; }    // Payer account (masked)
    public string AccountName { get; set; }      // Payer name
    public string Method { get; set; }           // "CARD", "TRANSFER", "WALLET"
}
```

**Example**:
```csharp
var response = await _payOsService.GetCheckoutLinkDetailsAsync(
    orderCode: 2603300123456
);

if (response.Code == "00")
{
    var order = response.OrderData;
    switch (order.Status)
    {
        case "COMPLETED":
            // Payment received, process quota top-up
            await _quotaService.TopUpAsync(workspaceId, order.Amount);
            break;
        case "PENDING":
            // Still waiting for payment
            return new { status = "pending", expiresIn = order.ExpiredAt };
        case "CANCELLED":
            // Payment cancelled
            break;
    }
}
```

**Status Values**:
- `PENDING` - Checkout link created, waiting for payment
- `PROCESSING` - Payment received, processing
- `COMPLETED` - Payment successful and settled
- `CANCELLED` - Link expired or manually cancelled

---

### 3. CancelCheckoutLinkAsync

**Purpose**: Revoke an unpaid checkout link (usually on user request or timeout)

**Request Parameters**:
```csharp
long orderCode = 123456;  // Order to cancel
```

**Response**:
```csharp
public class PayOsCancelResponse
{
    public string Code { get; set; }             // "00" = success
    public string Desc { get; set; }             // "Successfully cancelled"
    public object Data { get; set; }             // Usually null/empty
}
```

**Example**:
```csharp
var response = await _payOsService.CancelCheckoutLinkAsync(
    orderCode: 2603300123456
);

if (response.Code == "00")
{
    _logger.LogInformation($"Checkout {orderCode} cancelled");
    return Ok(new { message = "Checkout link cancelled" });
}
else
{
    // Already paid or other error
    throw new PaymentException(response.Desc);
}
```

**When to Use**:
- User navigates away from payment page
- Payment timeout (>15 minutes)
- Admin cancels order
- Order is no longer valid

---

### 4. RefundAsync

**Purpose**: Issue a full or partial refund for a completed payment

**Request Parameters**:
```csharp
string orderCode = "2603300123456";  // Must be string for API
long amount = 199000;                // Refund amount in VND
string reason = "User requested refund - subscription change";  // For records
```

**Response**:
```csharp
public class PayOsRefundResponse
{
    public string Code { get; set; }               // "00" = success
    public string Desc { get; set; }               // Status message
    public PayOsRefundData RefundData { get; set; }  // Refund details
}

public class PayOsRefundData
{
    public string RefundId { get; set; }           // Unique refund ID
    public string OrderCode { get; set; }          // Original order
    public long RefundAmount { get; set; }         // Refunded amount
    public string Status { get; set; }             // "SUCCESSFUL", "FAILED", "PENDING"
    public DateTime CreatedAt { get; set; }        // Refund timestamp (UTC)
}
```

**Example**:
```csharp
var response = await _payOsService.RefundAsync(
    orderCode: "2603300123456",
    amount: 199000,
    reason: "Customer downgraded to free plan"
);

if (response.Code == "00")
{
    var refund = response.RefundData;
    _logger.LogInformation(
        $"Refund {refund.RefundId} for order {refund.OrderCode}: {refund.RefundAmount} VND");
    
    // Deduct quota or process cancellation
    await _quotaService.DeductAsync(workspaceId, refund.RefundAmount);
}
else
{
    _logger.LogError($"Refund failed: {response.Desc}");
    throw new RefundException(response.Desc);
}
```

**Refund Status**:
- `SUCCESSFUL` - Refund processed, customer received funds (3-5 business days)
- `FAILED` - Refund rejected (customer account issue, amount mismatch, etc.)
- `PENDING` - Refund queued, processing

---

## Configuration

### Development (Sandbox)

```json
{
  "PayOS": {
    "ClientId": "sandbox_3de....",
    "ApiKey": "key_live_3de....",
    "ChecksumKey": "checksum_3de....",
    "BaseUrl": "https://api-sandbox.payos.vn",
    "IsProduction": false
  }
}
```

### Production

```json
{
  "PayOS": {
    "ClientId": "prod_3de....",
    "ApiKey": "key_prod_3de....",
    "ChecksumKey": "checksum_prod....",
    "BaseUrl": "https://api.payos.vn",
    "IsProduction": true
  }
}
```

**All credentials are read from `IConfiguration`:**
```csharp
private readonly IConfiguration _configuration;

public PayOsService(HttpClient httpClient, IConfiguration configuration)
{
    _clientId = configuration["PayOS:ClientId"];
    _apiKey = configuration["PayOS:ApiKey"];
    _checksumKey = configuration["PayOS:ChecksumKey"];
    _baseUrl = configuration["PayOS:BaseUrl"];
}
```

---

## Error Handling

### Common Exceptions

```csharp
try
{
    var response = await _payOsService.CreateCheckoutLinkAsync(...);
}
catch (HttpRequestException ex)
{
    // Network error (no internet, PayOS API down)
    _logger.LogError($"Network error: {ex.Message}");
    // Retry with exponential backoff
}
catch (TaskCanceledException ex)
{
    // Request timeout (>30 seconds)
    _logger.LogError($"Timeout: {ex.Message}");
    // Don't retry, inform user
}
catch (JsonException ex)
{
    // Invalid JSON response from PayOS
    _logger.LogError($"Invalid response: {ex.Message}");
    // Contact PayOS support
}
```

### Response Error Codes

| Code | Meaning | Action |
|------|---------|--------|
| `00` | Success | Process normally |
| `01` | Invalid request | Check parameters (amount, order code, URLs) |
| `02` | Auth failed | Verify credentials in config |
| `03` | Order exists | Use different orderCode, wait 24 hours |
| `04` | Amount invalid | Amount must be ≥ 1,000 VND |
| `50` | Server error | Retry after 5 seconds |

---

## Usage in BusinessService

### Example: Create Payment Link Endpoint

```csharp
[HttpPost("checkout")]
public async Task<IActionResult> CreateCheckoutAsync(
    [FromBody] CreatePaymentLinkRequest request,
    CancellationToken ct)
{
    // Validate request
    if (request.PlanId == Guid.Empty)
        return BadRequest("planId is required");

    // Get workspace from header
    var workspaceId = Guid.Parse(Request.Headers["X-Workspace-Id"]);

    // Get plan pricing
    var plan = await _planService.GetByIdAsync(request.PlanId, ct);
    var amount = (long)(plan.PriceVnd * 100);  // Convert to cents

    // Generate unique order code (timestamp + random)
    var orderCode = (long)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() * 1000 + Random.Shared.Next(999));

    // Create checkout via PayOS
    var response = await _payOsService.CreateCheckoutLinkAsync(
        orderCode: orderCode,
        amount: amount,
        description: $"Upgrade to {plan.Name}",
        returnUrl: "https://app.warptalk.vn/checkout/success",
        cancelUrl: "https://app.warptalk.vn/checkout/cancel",
        cancellationToken: ct
    );

    if (response.Code != "00")
        throw new PaymentException($"PayOS error: {response.Desc}");

    // Create transaction record
    var transaction = new Transaction
    {
        WorkspaceId = workspaceId,
        OrderCode = orderCode,
        Amount = plan.PriceVnd,
        Status = TransactionStatus.Pending,
        CreatedAt = DateTime.UtcNow
    };
    await _transactionRepo.AddAsync(transaction, ct);
    await _unitOfWork.SaveChangesAsync(ct);

    // Return checkout URL to frontend
    return Ok(new PaymentLinkResponse
    {
        CheckoutUrl = response.Data.CheckoutUrl,
        QrCode = response.Data.QrCode,
        OrderCode = orderCode,
        Status = "Pending"
    });
}
```

### Example: Webhook Handler

```csharp
[HttpPost("payos/webhook")]
[AllowAnonymous]
public async Task<IActionResult> HandlePayOsWebhookAsync(
    [FromBody] PayOsWebhookPayload payload,
    CancellationToken ct)
{
    // 1. Verify signature (HMAC-SHA256)
    var isValid = VerifySignature(payload, _checksumKey);
    if (!isValid)
    {
        _logger.LogWarning("Invalid PayOS webhook signature");
        return BadRequest("Invalid signature");
    }

    // 2. Process based on status
    var transaction = await _transactionRepo.GetByOrderCodeAsync(payload.OrderCode, ct);
    if (transaction == null)
        return NotFound();

    if (payload.Code == "00" && payload.Data.OrderData.Status == "COMPLETED")
    {
        // 3. Payment successful - top up quota
        transaction.Status = TransactionStatus.Success;
        transaction.CompletedAt = DateTime.UtcNow;

        await _quotaService.TopUpAsync(
            transaction.WorkspaceId,
            transaction.Amount,
            ct
        );

        _logger.LogInformation(
            $"Payment successful: Order {payload.OrderCode}, " +
            $"Amount: {transaction.Amount} VND, " +
            $"Workspace: {transaction.WorkspaceId}"
        );
    }
    else
    {
        // 4. Payment failed
        transaction.Status = TransactionStatus.Failed;
    }

    await _unitOfWork.SaveChangesAsync(ct);
    return Ok(new { message = "Webhook processed" });
}
```

---

## Testing

### Unit Tests (Mocking)

```csharp
[Fact]
public async Task CreatePaymentLink_WhenValidRequest_ShouldReturnCheckoutUrl()
{
    // Arrange
    var payOsServiceMock = new Mock<IPayOsService>();
    payOsServiceMock
        .Setup(p => p.CreateCheckoutLinkAsync(
            It.IsAny<long>(),
            It.IsAny<long>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()))
        .ReturnsAsync(new PayOsCheckoutResponse
        {
            Code = "00",
            Desc = "Success",
            Data = new PayOsCheckoutData
            {
                CheckoutUrl = "https://pay.payos.vn/web/test",
                QrCode = "data:image/png;base64,..."
            }
        });

    var service = new PaymentService(payOsServiceMock.Object, ...);

    // Act
    var response = await service.CreatePaymentLinkAsync(
        Guid.NewGuid(),
        new CreatePaymentLinkRequest { PlanId = Guid.NewGuid() },
        CancellationToken.None
    );

    // Assert
    response.Should().NotBeNull();
    response.CheckoutUrl.Should().StartWith("https://pay.payos.vn");
}
```

### Integration Tests (Real API)

Use PayOS Sandbox API with test card: `4111111111111111`

---

## Troubleshooting

| Issue | Solution |
|-------|----------|
| Invalid signature error | Check ChecksumKey in config matches PayOS dashboard |
| Order not found | Use Postman to check order via GetCheckoutLinkDetailsAsync |
| Amount mismatch | Ensure amount is in VND (not cents), minimum 1,000 |
| Timeout errors | Increase HttpClient timeout, check internet connection |
| Credentials expired | Rotate API keys in PayOS dashboard, update config |

---

## See Also

- [PAYOS_SETUP_GUIDE.md](./PAYOS_SETUP_GUIDE.md) - Setup & Ngrok tunneling
- [PAYOS_QUICK_START.md](./PAYOS_QUICK_START.md) - Quick start checklist
- [PayOS Developer Docs](https://developers.payos.vn)
- [PaymentService.cs](../src/WarpTalk.BillingService.Application/Services/PaymentService.cs) - Usage examples

---

**Last Updated**: April 30, 2026  
**PayOS API Version**: v1 (Sandbox + Production)
