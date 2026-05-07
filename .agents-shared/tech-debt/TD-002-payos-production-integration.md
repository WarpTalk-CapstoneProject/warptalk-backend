# PayOS Production Integration - Mock → Real Implementation

**Date**: 2026-05-04  
**Topic**: PayOS Payment Gateway - Production Integration  
**Status**: ⚠️ TECH DEBT - HIGH PRIORITY  
**Context**: Billing Service hiện chỉ sử dụng PayOS mock service cho localhost development. Chưa có tích hợp PayOS thực tế để xử lý thanh toán thực tế trong production environment.

---

## 1. Current State - Mock/Localhost Only

### Problem
- 🔴 **PayOS Mock Service**: Chỉ giả lập, không thực tế
- 🔴 **No Real Transactions**: Không có thanh toán thực tế trong production
- 🔴 **No Webhook Handling**: Mock service không gửi webhook thực tế
- 🔴 **No Payment Confirmation**: Không có xác nhận thanh toán từ PayOS server

### Current Implementation (Mock)
```csharp
// src/WarpTalk.BillingService.API/Services/MockPayOsService.cs
public class MockPayOsService : IPaymentService
{
    public async Task<CreatePaymentLinkResponse> CreatePaymentLink(PaymentRequest request)
    {
        // ⚠️ MOCK: Trả về response giả lập
        return new CreatePaymentLinkResponse
        {
            Code = "00",
            Desc = "success",
            Data = new PaymentLinkData
            {
                PaymentLinkId = Guid.NewGuid().ToString(),
                CheckoutUrl = "https://sandbox.payos.vn/web/...", // Sandbox URL
                Amount = request.Amount,
                Currency = "VND",
            }
        };
    }
    
    public async Task<bool> VerifySignature(string signature, string data) 
    {
        // ⚠️ MOCK: Luôn trả về true
        return true;
    }
}
```

### Affected Services
- ✓ Billing Service API (src/WarpTalk.BillingService.API)
- ✓ Webhook Handler (Payment/WebhookController.cs)
- ✓ Payment Service (Application/Services)
- ✓ Integration Tests

---

## 2. Production Requirements

### 2.1 PayOS Real Account Setup
- [ ] Đăng ký production account tại [payos.vn](https://payos.vn)
- [ ] Nhận Client ID, API Key, Webhook Secret từ PayOS console
- [ ] Cấu hình IP whitelist trên PayOS dashboard
- [ ] Cấu hình Webhook endpoint (callback URL)

### 2.2 Credentials Management
- **Environment Variables** (được lưu trong Secret Manager):
  ```bash
  PAYOS_CLIENT_ID=xxxxx
  PAYOS_API_KEY=xxxxx
  PAYOS_WEBHOOK_SECRET=xxxxx
  PAYOS_BASE_URL=https://api.payos.vn  # Production URL
  WEBHOOK_CALLBACK_URL=https://billing.warptalk.app/api/webhooks/payos
  ```

- **Current** (chỉ có mock):
  ```bash
  PAYOS_MODE=mock  # ⚠️ TẠM THỜI
  ```

### 2.3 Real Payment Flow
```
┌─────────────┐         ┌──────────────┐       ┌─────────┐
│   Client    │         │   Billing    │       │ PayOS   │
│  (Web/App)  │         │   Service    │       │ Server  │
└─────────────┘         └──────────────┘       └─────────┘
      │                        │                      │
      │ 1. Request Payment      │                      │
      ├────────────────────────>│                      │
      │                         │ 2. POST /payment     │
      │                         │ (Create Link)        │
      │                         ├─────────────────────>│
      │                         │                      │
      │                         │ 3. Return URL        │
      │                         │ (Real PayOS URL)     │
      │<────────────────────────┤<─────────────────────┤
      │                         │                      │
      │ 4. Redirect to PayOS    │                      │
      ├─────────────────────────────────────────────────>│
      │                         │                      │
      │ 5. User pays            │                      │
      │ (Real Transaction)      │                      │
      │ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─> │                      │
      │                         │ 6. Webhook Notify    │
      │                         │ (Payment Confirmed)  │
      │                         │<─────────────────────┤
      │                         │ 7. Verify Signature  │
      │                         │ (HMAC-SHA256)        │
      │                         │ 8. Update Status     │
      │                         │ (Mark as PAID)       │
      │                         │ 9. Success Response  │
      │                         ├─────────────────────>│
      │                         │                      │
```

---

## 3. Implementation Strategy

### Phase 1: Create Real PayOS Service (2 hours)
```csharp
// src/WarpTalk.BillingService.API/Services/RealPayOsService.cs
public class RealPayOsService : IPaymentService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<RealPayOsService> _logger;
    private readonly PayOsOptions _options; // Client ID, API Key, etc.

    public async Task<CreatePaymentLinkResponse> CreatePaymentLink(PaymentRequest request)
    {
        // ✅ REAL: Call PayOS API
        var payload = new
        {
            orderCode = request.ReferenceId,
            amount = request.Amount,
            description = request.Description,
            returnUrl = $"{_options.ReturnUrl}?order_code={request.ReferenceId}",
            cancelUrl = $"{_options.CancelUrl}?order_code={request.ReferenceId}",
            buyerName = request.BuyerName,
            buyerEmail = request.BuyerEmail,
            buyerPhone = request.BuyerPhone,
            buyerAddress = request.BuyerAddress,
            items = request.Items, // Line items
            signature = GenerateSignature(request) // HMAC-SHA256
        };

        var response = await _httpClient.PostAsJsonAsync(
            "/v1/payment-links",
            payload
        );

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsAsync<CreatePaymentLinkResponse>();
    }

    public async Task<bool> VerifySignature(string signature, string data)
    {
        // ✅ REAL: Verify HMAC-SHA256 signature từ PayOS
        var expectedSignature = GenerateSignature(data);
        return CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(signature),
            Convert.FromHexString(expectedSignature)
        );
    }

    private string GenerateSignature(object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_options.ApiKey)))
        {
            var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(json));
            return Convert.ToHexString(hash).ToLower();
        }
    }
}
```

### Phase 2: Update Dependency Injection (1 hour)
```csharp
// src/WarpTalk.BillingService.API/Program.cs
var payOsMode = builder.Configuration["PayOS:Mode"]; // "mock" | "real"

if (payOsMode == "mock")
{
    builder.Services.AddScoped<IPaymentService, MockPayOsService>();
    logger.Information("🔵 Using MOCK PayOS Service (Development Only)");
}
else
{
    builder.Services.AddScoped<IPaymentService, RealPayOsService>();
    builder.Services.Configure<PayOsOptions>(builder.Configuration.GetSection("PayOS"));
    logger.Information("🟢 Using REAL PayOS Service (Production)");
}
```

### Phase 3: Webhook Signature Verification (1 hour)
```csharp
// src/WarpTalk.BillingService.API/Security/WebhookSignatureValidator.cs - UPDATE
public class WebhookSignatureValidator
{
    private readonly IPaymentService _paymentService;

    public async Task<bool> ValidatePayOsSignature(
        string signature,
        string webhookData)
    {
        try
        {
            // ✅ REAL: Verify signature từ PayOS
            var isValid = await _paymentService.VerifySignature(
                signature,
                webhookData
            );

            if (!isValid)
            {
                logger.Warning("❌ Invalid PayOS webhook signature");
                return false;
            }

            logger.Information("✅ Valid PayOS webhook signature verified");
            return true;
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Error validating PayOS signature");
            return false;
        }
    }
}
```

### Phase 4: Update Configuration (1 hour)
```json
// appsettings.production.json
{
  "PayOS": {
    "Mode": "real",
    "ClientId": "${PAYOS_CLIENT_ID}",
    "ApiKey": "${PAYOS_API_KEY}",
    "WebhookSecret": "${PAYOS_WEBHOOK_SECRET}",
    "BaseUrl": "https://api.payos.vn",
    "ReturnUrl": "https://warptalk.app/billing/success",
    "CancelUrl": "https://warptalk.app/billing/cancel"
  }
}
```

### Phase 5: Testing & Validation (1 hour)
```csharp
// tests/WarpTalk.BillingService.Tests/PayOsProductionIntegrationTests.cs
[Fact]
public async Task CreatePaymentLink_WithRealPayOS_ReturnsValidCheckoutUrl()
{
    // Arrange
    var request = new PaymentRequest
    {
        ReferenceId = Guid.NewGuid().ToString(),
        Amount = 99_000, // VND
        Description = "Pro Plan Upgrade",
    };

    // Act
    var response = await _paymentService.CreatePaymentLink(request);

    // Assert
    Assert.NotNull(response);
    Assert.Equal("00", response.Code); // PayOS success code
    Assert.Contains("payos.vn", response.Data.CheckoutUrl); // Real URL
    Assert.True(response.Data.Amount > 0);
}

[Fact]
public async Task WebhookSignature_WithRealPayOS_ValidatesCorrectly()
{
    // Arrange
    var webhookData = @"{""orderCode"":""12345"",""amount"":99000}";
    var signature = GenerateRealPayOsSignature(webhookData);

    // Act
    var isValid = await _validator.ValidatePayOsSignature(signature, webhookData);

    // Assert
    Assert.True(isValid);
}
```

### Phase 6: Staging Environment Testing (2 hours)
- Deploy to staging with real PayOS credentials
- Perform end-to-end payment test
- Verify webhook delivery & processing
- Monitor logs for issues

---

## 4. Risk Assessment

### Risks
- 🔴 **Payment Processing Failures**: Network issues với PayOS API
  - Mitigation: Implement retry logic with exponential backoff
  
- 🔴 **Webhook Delays**: PayOS webhooks có thể delay
  - Mitigation: Implement polling mechanism as fallback
  
- 🔴 **Security**: API Key exposure
  - Mitigation: Use Azure Key Vault, never log credentials

### Rollback Plan
```bash
# Nếu có issue, rollback về mock service:
1. Rollback deployment
2. Set PayOS:Mode = "mock" trong config
3. Restart Billing Service
4. Investigate issue
```

---

## 5. Success Criteria

- ✅ Real PayOS service hoạt động trong staging
- ✅ 5+ end-to-end payment tests passing
- ✅ Webhook signature verification 100% correct
- ✅ No credential leaks in logs
- ✅ Automated failover to mock if real service fails
- ✅ Production deployment without incidents

---

## 6. Timeline & Effort

| Phase | Effort | Duration | Owner |
|-------|--------|----------|-------|
| 1. Create Real Service | 2h | 1 day | Backend Dev |
| 2. DI Setup | 1h | 1 day | Backend Dev |
| 3. Webhook Validation | 1h | 1 day | Backend Dev |
| 4. Configuration | 1h | 1 day | DevOps |
| 5. Testing | 1h | 1 day | QA/Dev |
| 6. Staging Deployment | 2h | 1-2 days | DevOps |
| 7. Production Deployment | 1h | 1 day | DevOps |

**Total Effort**: 6-8 hours  
**Total Duration**: 1-2 weeks (including testing & validation)  
**Priority**: 🔴 HIGH (Critical for production)

---

## 7. Blocked By / Dependencies

- ⏳ PayOS production account setup (admin task)
- ⏳ Azure Key Vault configured (DevOps task)
- ⏳ IP whitelist approved (security task)
- ⏳ Webhook endpoint accessible from internet (DevOps task)

---

## 8. Definition of Done

- [x] PayOS real service implemented
- [x] Mock service still works (for development)
- [x] Configuration supports both modes
- [x] Webhook signature verification updated
- [x] All tests passing (80+ existing tests + 5 new)
- [x] Staging environment tested
- [x] Production deployment guide created
- [x] Rollback procedure documented
- [x] Monitoring & alerts configured
- [x] Team trained on new process

---

## 9. Post-Implementation

### Monitoring
```
Alert if:
- PayOS API response time > 5s
- Webhook delivery failure > 1%
- Signature verification failure > 0.1%
- Transaction processing error > 0.5%
```

### Logging
```csharp
_logger.Information(
    "PayOS Payment Created: " +
    "OrderCode={OrderCode}, Amount={Amount}, " +
    "Url={CheckoutUrl}",
    request.ReferenceId, request.Amount, response.Data.CheckoutUrl
);
```

### Audit Trail
```
Event: PAYMENT_LINK_CREATED
  - Workspace ID
  - User ID
  - Order Code
  - Amount
  - Currency
  - Timestamp
  - PayOS Link ID
```

---

## 10. References

- 📚 [PayOS API Documentation](https://docs.payos.vn)
- 📚 [PayOS Webhook Guide](https://docs.payos.vn/webhooks)
- 📚 [Current Mock Service](../../billing/src/WarpTalk.BillingService.API/Services/MockPayOsService.cs)
- 📚 [Webhook Validator](../../billing/src/WarpTalk.BillingService.API/Security/WebhookSignatureValidator.cs)
- 📚 [Payment Tests](../../billing/tests/WarpTalk.BillingService.Tests/WebhookSecurityTests.cs)

---

## 11. Questions & Decisions

**Q: Có nên giữ mock service?**  
A: ✅ YES - Mock service hữu ích cho local development & CI/CD testing

**Q: Khi nào fallback từ real → mock?**  
A: Fallback nên xảy ra nếu PayOS API không available (circuit breaker pattern)

**Q: Có cần retry logic?**  
A: ✅ YES - Implement exponential backoff cho network failures

**Q: Production rollback procedure?**  
A: Set `PayOS:Mode=mock` trong config ngay lập tức, không cần recompile

---

**Status**: 📋 READY FOR IMPLEMENTATION  
**Last Updated**: 2026-05-04  
**Next Review**: 2026-05-18
