# PayOS Sandbox Setup - Ngrok + Real Backend

## Phần 1: Đăng ký PayOS Sandbox

### 1.1 Tạo tài khoản PayOS
- Truy cập: https://dashboard.payos.vn
- Đăng ký tài khoản merchant
- Xác thực email

### 1.2 Lấy Sandbox Credentials
1. Vào **Settings → API Keys**
2. Copy 3 giá trị:
   - **Client ID** (e.g., `5de161cf-...)
   - **API Key** (e.g., `00820c7d-...`)
   - **Checksum Key** (e.g., `abc123def456...`)

⚠️ **Lưu ý**: Sandbox keys khác Production keys!

### 1.3 Cấu hình Webhook URL
1. Vào **Settings → Webhooks**
2. Cấp nhật **Webhook URL**: 
   - `https://<your-ngrok-url>/api/v1/billing/payos/webhook`
   - VD: `https://abc123.ngrok.io/api/v1/billing/payos/webhook`
3. Activate webhook

---

## Phần 2: Setup Ngrok

### 2.1 Cài Ngrok
```powershell
# Windows - Download từ https://ngrok.com/download
# hoặc dùng Chocolatey
choco install ngrok

# Verify
ngrok --version
```

### 2.2 Authenticate Ngrok (Nếu cần)
```powershell
ngrok config add-authtoken <your_ngrok_token>
```
(Lấy từ https://dashboard.ngrok.com/auth)

### 2.3 Start Ngrok Tunnel
```powershell
# Tunnel port 8080 (ASP.NET Core default)
ngrok http 8080

# Output:
# Forwarding   https://abc123.ngrok.io -> http://localhost:8080
```

📌 **Lưu URL này**: `https://abc123.ngrok.io`

---

## Phần 3: Cấu hình Billing Service

### 3.1 Update appsettings.Development.json
```json
{
  "PayOS": {
    "ClientId": "YOUR_SANDBOX_CLIENT_ID",
    "ApiKey": "YOUR_SANDBOX_API_KEY",
    "ChecksumKey": "YOUR_SANDBOX_CHECKSUM_KEY",
    "BaseUrl": "https://api-sandbox.payos.vn",
    "IsProduction": false
  },
  "Security": {
    "AllowInsecureWebhookSignatureInDevelopment": false
  },
  "Webhook": {
    "ReturnUrl": "https://abc123.ngrok.io/api/v1/billing/checkout/return"
  }
}
```

### 3.2 Cài PayOS C# SDK
```bash
cd d:\WarpTalk\warptalk-backend\billing
dotnet add package Net.payOS --project src/WarpTalk.BillingService.Application
```

### 3.3 Verify Build
```bash
dotnet build WarpTalk.BillingService.sln
```

---

## Phần 4: Run Billing Service

### 4.1 Start API
```bash
cd d:\WarpTalk\warptalk-backend\billing
dotnet run --project src/WarpTalk.BillingService.API
```

Output:
```
info: Microsoft.AspNetCore.Hosting.Diagnostics[1]
      Request starting HTTP/1.1 POST https://localhost:5445
      Now listening on: https://localhost:5445
```

### 4.2 Verify Health Check
```bash
curl https://localhost:5445/health
```

---

## Phần 5: Test Flow

### 5.1 Create Payment Link (via Postman)
```
POST https://abc123.ngrok.io/api/v1/billing/checkout
X-Workspace-Id: 77777777-7777-7777-7777-777777777777

{
  "planId": "22222222-2222-2222-2222-222222222222"
}
```

Response:
```json
{
  "checkoutUrl": "https://pay.payos.vn/web/...",
  "orderCode": 2603300123456,
  "status": "Pending"
}
```

### 5.2 Open Checkout Link
- Copy `checkoutUrl`
- Open in browser: `https://pay.payos.vn/web/...`
- Use PayOS test card:
  - **Card Number**: `4111111111111111`
  - **Expiry**: `12/25`
  - **CVV**: `123`
  - **OTP**: `123456`

### 5.3 Verify Webhook Received
- Check service logs:
  ```
  info: WarpTalk.BillingService.Application.Services.PaymentService[0]
        Successfully updated transaction 2603300123456 to Success
  ```

- Query database:
  ```sql
  SELECT OrderCode, Status, CompletedAt FROM transactions 
  ORDER BY CreatedAt DESC LIMIT 1;
  ```

---

## Phần 6: Troubleshooting

### ❌ Webhook không nhận được
- **Ngrok tunnel chưa start**: `ngrok http 8080`
- **Webhook URL sai**: Kiểm tra Settings → Webhooks
- **Firewall chặn**: Thêm exception cho ngrok
- **Logs**: Kiểm tra `GetUsageLogs` endpoint

### ❌ "Invalid Signature"
- Checksum Key không đúng → Copy lại từ PayOS Dashboard
- Checksum Key đã change → Update `appsettings.Development.json`

### ❌ "Order not found"
- Order được lưu nhưng không tìm thấy
- Kiểm tra database: `SELECT * FROM transactions WHERE order_code = 123;`

### ❌ CORS Error
- Confirm `Cors:AllowedOrigins` include webhook domain
- hoặc webhook không qua CORS (backend-to-backend)

---

## Phần 7: Logs & Debugging

### 7.1 View Transaction Details
```bash
# Get all transactions
curl https://abc123.ngrok.io/api/v1/billing/transactions \
  -H "X-Workspace-Id: 77777777-7777-7777-7777-777777777777"

# Response:
[
  {
    "orderCode": 2603300123456,
    "status": "Success",
    "amountVnd": 199000,
    "purchasedMinutes": 500,
    "completedAt": "2026-04-30T15:30:45Z"
  }
]
```

### 7.2 View Audit Logs
```bash
curl https://abc123.ngrok.io/api/v1/billing/quota/usage-logs \
  -H "X-Workspace-Id: 77777777-7777-7777-7777-777777777777"
```

### 7.3 Check Correlation IDs
- Mỗi request có `X-Correlation-Id` header
- Log files include correlation ID cho trace
- Dùng correlation ID để debug end-to-end

---

## Phần 8: Production Deployment

### 8.1 Lấy Production Credentials
- Yêu cầu từ PayOS support
- Khác biệt: 
  - Client ID khác
  - API Key khác
  - Checksum Key khác
  - BaseUrl: `https://api.payos.vn` (không có -sandbox)

### 8.2 Update appsettings.json (Production)
```json
{
  "PayOS": {
    "BaseUrl": "https://api.payos.vn",
    "IsProduction": true
  }
}
```

### 8.3 Webhook URL (Production)
- Update Settings → Webhooks
- URL: `https://your-domain.com/api/v1/billing/payos/webhook`

---

## Checklist

- ✅ PayOS Sandbox account created
- ✅ Client ID, API Key, Checksum Key copied
- ✅ Webhook URL configured in PayOS Dashboard
- ✅ Ngrok installed and running
- ✅ Net.payOS SDK installed
- ✅ appsettings.Development.json updated
- ✅ Billing Service running on port 8080
- ✅ ngrok tunnel active on port 8080
- ✅ Health check endpoint responding
- ✅ Test payment created and completed
- ✅ Webhook received and processed
- ✅ Transaction status updated to "Success"
- ✅ Audit logs showing quota update

---

## Next Steps

1. **Frontend Integration**: Add checkout button → payOS link
2. **Webhook Signature**: Already implemented with HMAC-SHA256
3. **Refund API**: Implement refund functionality
4. **Settlement**: Track settlement batches from PayOS

**Support**: support@payos.vn

---

*Setup Guide v1.0*  
*Last Updated: 2026-04-30*
