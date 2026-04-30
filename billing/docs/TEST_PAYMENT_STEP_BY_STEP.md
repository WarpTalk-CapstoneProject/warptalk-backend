# 🧪 Test Thanh Toán PayOS - Step by Step

## Hiện tại bạn đã có ✅
- ✅ Ngrok chạy trên port 8080
- ✅ Code PayOS hoàn chỉnh
- ✅ Postman collection sẵn sàng

## Bước 1️⃣: Lấy Credentials Sandbox PayOS (2 phút)

```
1. Truy cập: https://dashboard.payos.vn
2. Đăng nhập / Đăng ký tài khoản
3. Vào Settings → API Keys → Sandbox Tab
4. Copy 3 thứ:
   - Client ID:    ...
   - API Key:      ...
   - Checksum Key: ...
```

## Bước 2️⃣: Cấu hình Billing Service (1 phút)

**File**: `src/WarpTalk.BillingService.API/appsettings.Development.json`

```json
{
  "PayOS": {
    "ClientId": "PASTE_CLIENT_ID_HERE",
    "ApiKey": "PASTE_API_KEY_HERE",
    "ChecksumKey": "PASTE_CHECKSUM_KEY_HERE",
    "BaseUrl": "https://api-sandbox.payos.vn"
  },
  "Webhook": {
    "ReturnUrl": "http://localhost:3000/checkout/result",
    "CancelUrl": "http://localhost:3000/checkout/cancel"
  }
}
```

## Bước 3️⃣: Cấu hình Webhook trong PayOS Dashboard (2 phút)

**Ngrok URL của bạn hiện tại là gì?**
- Kiểm tra terminal ngrok → tìm dòng: `https://abc123.ngrok.io`

**Rồi thì:**
```
1. Truy cập: https://dashboard.payos.vn/settings/webhooks
2. Thêm Webhook URL: https://YOUR_NGROK_URL/api/v1/billing/payos/webhook
3. Save
```

Ví dụ:
```
https://2a3c5d7f-9b1e.ngrok.io/api/v1/billing/payos/webhook
```

## Bước 4️⃣: Khởi động Billing Service (1 phút)

**Terminal 1** (Keep ngrok running):
```
ngrok http 8080
# Giữ cửa sổ này mở!
```

**Terminal 2** (Start Billing Service):
```bash
cd d:\WarpTalk\warptalk-backend\billing

dotnet run --project src/WarpTalk.BillingService.API
```

**Khi khởi động xong sẽ thấy:**
```
info: WarpTalk.BillingService.API.Program[0]
      Now listening on: https://localhost:5445
info: PayOS Service initialized with sandbox API
```

## Bước 5️⃣: Test qua Postman (5 phút)

### 5a. Import Collection

```
1. Mở Postman
2. Vào: File → Import
3. Chọn file: postman/WarpTalk.Billing.PayOS.Sandbox.postman_collection.json
4. Import
```

### 5b. Update Variables

```
1. Vào tab: Collection → WarpTalk Billing
2. Edit Variables:
   - base_url: https://YOUR_NGROK_URL
   - workspace_id: 77777777-7777-7777-7777-777777777777 (dùng cái có sẵn cũng được)
3. Save
```

### 5c. Test Endpoint: "Create Payment Link"

**Request:**
```
POST {{base_url}}/api/v1/billing/checkout
Headers: X-Workspace-Id: {{workspace_id}}
Body:
{
  "planId": "22222222-2222-2222-2222-222222222222"
}
```

**Response mong đợi:**
```json
{
  "checkoutUrl": "https://pay.payos.vn/web/...",
  "orderCode": 2603300123456,
  "status": "Pending"
}
```

## Bước 6️⃣: Hoàn thành thanh toán (3 phút) 💳

### 6a. Lấy Checkout URL

```
1. Copy giá trị "checkoutUrl" từ response Postman
2. Mở nó trong browser
```

### 6b. Điền thông tin thẻ (Test Card)

```
Card Number:  4111111111111111
Expiry:       12/25
CVV:          123
OTP:          123456 (nhập lại khi được yêu cầu)
```

### 6c. Hoàn thành thanh toán

```
- Click "Thanh toán" / "Pay"
- Xác nhận OTP
- Chờ 3-5 giây để webhook xử lý
```

## Bước 7️⃣: Xác minh thành công ✅

### Kiểm tra 1: Logs của Billing Service

**Đã thấy dòng này chưa?**
```
info: WarpTalk.BillingService.API.Controllers.PayOsController[0]
      Webhook received for order 2603300123456
info: WarpTalk.BillingService.Application.Services.PaymentService[0]
      Processing payment: Order 2603300123456, Status COMPLETED
info: WarpTalk.BillingService.Application.Services.PaymentService[0]
      Payment successful, quota topped up
```

### Kiểm tra 2: Database

```bash
# Kết nối database qua DBeaver / psql
SELECT * FROM transactions 
WHERE order_code = 2603300123456;

# Kết quả:
# order_code | workspace_id | status    | amount | completed_at
# 2603300123456 | 77777777... | SUCCESS   | 199000 | 2026-04-30 15:30:45
```

### Kiểm tra 3: Quota

```bash
# Check usage_quotas table
SELECT * FROM usage_quotas 
WHERE workspace_id = '77777777-7777-7777-7777-777777777777';

# Kết quả:
# quota_balance sẽ tăng thêm (phụ thuộc plan)
```

### Kiểm tra 4: API Endpoint (Postman)

```
GET {{base_url}}/api/v1/billing/checkout/2603300123456/status
Headers: X-Workspace-Id: {{workspace_id}}

Response:
{
  "orderCode": 2603300123456,
  "status": "Success",
  "amountVnd": 199000,
  "completedAt": "2026-04-30T15:30:45Z"
}
```

---

## 🔥 Nếu gặp lỗi

### ❌ "Webhook not received"

```
✓ Ngrok tunnel vẫn chạy?
✓ Ngrok URL đã được lưu ở PayOS Dashboard?
✓ URL format đúng: https://abc123.ngrok.io/api/v1/billing/payos/webhook
✓ Billing Service console có lỗi không?
```

### ❌ "Invalid Signature"

```
✓ Checksum Key trong appsettings.Development.json có đúng không?
✓ Copy từ PayOS Dashboard lại
✓ Không có khoảng trắng thừa
```

### ❌ "Order not found"

```
✓ Order code phải unique trong 24 giờ
✓ Dùng cái từ response Postman
✓ Check database xem có không
```

### ❌ "Connection refused"

```
✓ Billing Service có chạy không?
✓ Port 5445 có bị chiếm không?
✓ Check logs có exception không
```

---

## 📊 Flow Test Thanh Toán

```
┌─────────────────────────────────────────────┐
│ 1. Postman: Create Payment Link             │
│    POST /api/v1/billing/checkout            │
│    Response: checkoutUrl                    │
└──────────────┬──────────────────────────────┘
               │
               ▼
┌─────────────────────────────────────────────┐
│ 2. Browser: Open checkoutUrl                │
│    https://pay.payos.vn/web/...             │
│    Nhập thẻ, OTP, confirm thanh toán        │
└──────────────┬──────────────────────────────┘
               │
               ▼
┌─────────────────────────────────────────────┐
│ 3. PayOS: Process Payment                   │
│    ✓ Xác nhận thẻ                           │
│    ✓ Lấy tiền                               │
│    ✓ Gửi webhook                            │
└──────────────┬──────────────────────────────┘
               │
               ▼
┌─────────────────────────────────────────────┐
│ 4. Ngrok → Billing Service: Webhook         │
│    POST /api/v1/billing/payos/webhook       │
│    Body: { code: "00", data: {...} }        │
└──────────────┬──────────────────────────────┘
               │
               ▼
┌─────────────────────────────────────────────┐
│ 5. Billing Service: Process Webhook         │
│    ✓ Verify HMAC-SHA256 signature           │
│    ✓ Update transaction → SUCCESS           │
│    ✓ Top up quota                           │
│    ✓ Log audit trail                        │
└──────────────┬──────────────────────────────┘
               │
               ▼
┌─────────────────────────────────────────────┐
│ 6. Database: Update                         │
│    ✓ transactions: status = "SUCCESS"       │
│    ✓ usage_quotas: balance += amount        │
│    ✓ audit_logs: payment recorded           │
└─────────────────────────────────────────────┘
```

---

## ✅ Checklist

- [ ] PayOS Sandbox credentials copied
- [ ] appsettings.Development.json filled
- [ ] Webhook URL registered in PayOS Dashboard
- [ ] Ngrok tunnel running on port 8080
- [ ] Billing Service running on port 5445
- [ ] Postman collection imported & variables updated
- [ ] Create Payment Link endpoint tested
- [ ] Checkout URL opened in browser
- [ ] Payment completed with test card
- [ ] Webhook received (check Billing Service logs)
- [ ] Transaction status updated to SUCCESS
- [ ] Quota topped up in database
- [ ] API endpoint returns correct status

---

## 🎯 Nếu mọi thứ OK, bạn đã ✅:

1. ✅ Setup local PayOS sandbox development
2. ✅ Integrate with Ngrok for webhook tunneling
3. ✅ Tested real payment flow (except real money)
4. ✅ Verified webhook processing
5. ✅ Confirmed database updates

**Ready to deploy to production!** 🚀

---

**Support**: support@payos.vn  
**Docs**: https://developers.payos.vn  
**PayOS Dashboard**: https://dashboard.payos.vn
