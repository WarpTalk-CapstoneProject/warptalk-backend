# PayOS Sandbox Integration - Quick Start

## 🚀 Option A: Ngrok + PayOS Sandbox (Real Backend)

### Step 1: Get PayOS Sandbox Credentials (2 min)
1. Register: https://dashboard.payos.vn
2. Copy from **Settings → API Keys**:
   - `Client ID`
   - `API Key`
   - `Checksum Key`

### Step 2: Install & Run Ngrok (5 min)
```powershell
# Install ngrok (or download from https://ngrok.com/download)
choco install ngrok

# Authenticate (get token from https://dashboard.ngrok.com/auth)
ngrok config add-authtoken <YOUR_NGROK_TOKEN>

# Start tunnel to port 8080
ngrok http 8080
# Output: https://abc123.ngrok.io -> http://localhost:8080
```

**Save ngrok URL**: `https://abc123.ngrok.io`

### Step 3: Configure Billing Service
```bash
cd d:\WarpTalk\warptalk-backend\billing
```

Edit `src/WarpTalk.BillingService.API/appsettings.Development.json`:
```json
{
  "PayOS": {
    "ClientId": "YOUR_CLIENT_ID_HERE",
    "ApiKey": "YOUR_API_KEY_HERE",
    "ChecksumKey": "YOUR_CHECKSUM_KEY_HERE"
  },
  "Webhook": {
    "ReturnUrl": "https://abc123.ngrok.io/api/v1/billing/checkout/result",
    "CancelUrl": "https://abc123.ngrok.io/api/v1/billing/checkout/cancel"
  }
}
```

### Step 4: Register Webhook in PayOS Dashboard
1. Go to **Settings → Webhooks**
2. Update URL: `https://abc123.ngrok.io/api/v1/billing/payos/webhook`
3. Save

### Step 5: Start Services
```bash
# Terminal 1: Ngrok tunnel
ngrok http 8080

# Terminal 2: Billing API
cd d:\WarpTalk\warptalk-backend\billing
dotnet run --project src/WarpTalk.BillingService.API
# Output: Now listening on: https://localhost:5445
```

### Step 6: Test Payment Flow
#### Via Postman:
1. Import: `postman/WarpTalk.Billing.PayOS.Sandbox.postman_collection.json`
2. Update `base_url` variable: `https://abc123.ngrok.io`
3. Run: `1. Create Payment Link (Plan Upgrade)`
4. Copy `checkoutUrl` from response

#### Or Via cURL:
```bash
curl -X POST https://abc123.ngrok.io/api/v1/billing/checkout \
  -H "Content-Type: application/json" \
  -H "X-Workspace-Id: 77777777-7777-7777-7777-777777777777" \
  -d '{
    "planId": "22222222-2222-2222-2222-222222222222"
  }'

# Response:
# {
#   "checkoutUrl": "https://pay.payos.vn/web/...",
#   "orderCode": 2603300123456,
#   "status": "Pending"
# }
```

### Step 7: Complete Payment in PayOS
1. Open `checkoutUrl` in browser
2. Use test card:
   - **Card**: `4111111111111111`
   - **Expiry**: `12/25`
   - **CVV**: `123`
   - **OTP**: `123456`
3. Complete payment
4. Wait ~5 seconds for webhook

### Step 8: Verify Success
```bash
# Check transaction status
curl https://abc123.ngrok.io/api/v1/billing/checkout/2603300123456/status \
  -H "X-Workspace-Id: 77777777-7777-7777-7777-777777777777"

# Expected response:
# {
#   "orderCode": 2603300123456,
#   "status": "Success",
#   "amountVnd": 199000,
#   "purchasedMinutes": 500,
#   "completedAt": "2026-04-30T15:30:45Z"
# }
```

---

## 🔧 Troubleshooting

### "webhook not received"
- ❌ Ngrok tunnel stopped → Restart: `ngrok http 8080`
- ❌ Wrong webhook URL in PayOS → Update: `https://abc123.ngrok.io/api/v1/billing/payos/webhook`
- ❌ Firewall blocking → Add ngrok exception

### "Invalid Signature"
- ❌ Wrong checksum key → Copy from PayOS Dashboard again
- ✅ Can use dev bypass: `Security:AllowInsecureWebhookSignatureInDevelopment=true` in dev only

### "Order not found"
- Check database: 
  ```sql
  SELECT * FROM transactions WHERE order_code = 2603300123456;
  ```

### Logs
```bash
# Real-time logs with correlation ID
dotnet run --project src/WarpTalk.BillingService.API

# Check transaction via API
curl https://localhost:5445/api/v1/billing/transactions \
  -H "X-Workspace-Id: 77777777-7777-7777-7777-777777777777" \
  -k
```

---

## 📊 Full Payment Flow

```
┌─────────────────┐
│  Frontend App   │
│  (React/Vue)    │
└────────┬────────┘
         │
         │ POST /api/v1/billing/checkout
         ├─ planId OR topUpMinutes
         ├─ X-Workspace-Id: workspace_guid
         │
         ▼
┌─────────────────────────────────────┐
│  Billing Service (localhost:8080)   │
│                                      │
│  1. Create Transaction (Pending)    │
│  2. Call PayOS.CreateCheckout       │
│  3. Return checkoutUrl              │
└────────┬────────────────────────────┘
         │
         │ Returns checkoutUrl
         │
         ▼
┌─────────────────┐
│  User Browser   │
│                 │
│  Open PayOS     │
│  Checkout Page  │
└────────┬────────┘
         │
         │ Enter card details + OTP
         │
         ▼
┌──────────────────────┐
│  PayOS Sandbox       │
│  Payment Processing  │
└────────┬─────────────┘
         │
         │ Webhook POST (PayOS → Ngrok → Local API)
         │
         ▼
┌────────────────────────────────────────┐
│  Billing Service Webhook Handler       │
│  /api/v1/billing/payos/webhook         │
│                                         │
│  1. Verify HMAC-SHA256 Signature       │
│  2. Update Transaction to "Success"    │
│  3. Top up Quota                       │
│  4. Log Audit Trail                    │
└────────────────────────────────────────┘
         │
         ▼
┌──────────────────┐
│  Database        │
│                  │
│ transactions: ✅  │
│ usage_quotas: ✅  │
│ audit_logs: ✅    │
└──────────────────┘
```

---

## 📝 Environment Variables

### Development
```bash
# appsettings.Development.json
{
  "PayOS": {
    "ClientId": "sandbox_client_id",
    "ApiKey": "sandbox_api_key",
    "ChecksumKey": "sandbox_checksum",
    "BaseUrl": "https://api-sandbox.payos.vn"
  },
  "Security": {
    "RequireAuthentication": false,
    "AllowInsecureWebhookSignatureInDevelopment": true
  }
}
```

### Production
```bash
# Environment variables
export PayOS__ClientId="prod_client_id"
export PayOS__ApiKey="prod_api_key"
export PayOS__ChecksumKey="prod_checksum"
export PayOS__BaseUrl="https://api.payos.vn"
export Security__RequireAuthentication="true"
export Security__AllowInsecureWebhookSignatureInDevelopment="false"
```

---

## ✅ Verification Checklist

- [ ] PayOS Sandbox account created
- [ ] Client ID, API Key, Checksum Key copied
- [ ] Webhook URL registered in PayOS
- [ ] Ngrok installed and authenticated
- [ ] `appsettings.Development.json` updated with credentials
- [ ] Ngrok tunnel running on port 8080
- [ ] Billing Service running on port 5445
- [ ] Health check endpoint responding: `curl https://localhost:5445/health -k`
- [ ] Payment link created successfully
- [ ] PayOS checkout page opened and payment completed
- [ ] Webhook received and processed
- [ ] Transaction status updated to "Success"
- [ ] Quota updated in database

---

## 🎯 Next Steps

1. **Frontend Integration**: Add checkout button linking to `checkoutUrl`
2. **Error Handling**: Implement retry logic for failed webhooks
3. **Settlement**: Track settlement batches from PayOS
4. **Refund API**: Implement refund functionality
5. **Production**: Switch credentials and deploy to staging

---

**Support**: support@payos.vn  
**Docs**: https://developers.payos.vn

*Last Updated: 2026-04-30*
