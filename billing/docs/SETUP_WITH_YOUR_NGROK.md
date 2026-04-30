# 🎯 Cấu hình PayOS Sandbox với Ngrok URL của Bạn

## 📍 Ngrok URL Của Bạn
```
https://4302-2001-ee0-50db-f6f0-f452-2c48-8a4d-e949.ngrok-free.app
```

**Webhook URL sẽ là:**
```
https://4302-2001-ee0-50db-f6f0-f452-2c48-8a4d-e949.ngrok-free.app/api/v1/billing/payos/webhook
```

---

## Bước 1️⃣: Lấy Credentials PayOS (2 phút)

### 1a. Truy cập PayOS Dashboard

Vào: **https://dashboard.payos.vn**

- Nếu chưa có tài khoản → Click **Đăng ký**
- Nếu có rồi → **Đăng nhập**

### 1b. Vào Settings → API Keys

```
Dashboard → Settings (⚙️) → API Keys → Sandbox Tab
```

### 1c. Copy 3 Credentials

Bạn sẽ thấy:
```
☑️ Sandbox (tab này)
  Client ID:     12c45678-1a2b-3c4d-5e6f-7g8h9i0j1k2l
  API Key:       key_live_xxxxxxxxxxxxxxxxxxxxxxxxxxxx
  Checksum Key:  checksum_xxxxxxxxxxxxxxxxxxxxxxxxxxxx
```

**Copy đúng 3 cái này!** (Không copy Production keys)

---

## Bước 2️⃣: Cấu hình appsettings.Development.json (1 phút)

**File**: `d:\WarpTalk\warptalk-backend\billing\src\WarpTalk.BillingService.API\appsettings.Development.json`

Tìm section `"PayOS"` và điền vào:

```json
{
  "PayOS": {
    "ClientId": "PASTE_CLIENT_ID_HERE",
    "ApiKey": "PASTE_API_KEY_HERE",
    "ChecksumKey": "PASTE_CHECKSUM_KEY_HERE",
    "BaseUrl": "https://api-sandbox.payos.vn",
    "IsProduction": false
  },
  "Webhook": {
    "ReturnUrl": "http://localhost:3000/checkout/result",
    "CancelUrl": "http://localhost:3000/checkout/cancel"
  }
}
```

**Ví dụ sau khi điền:**
```json
{
  "PayOS": {
    "ClientId": "12c45678-1a2b-3c4d-5e6f-7g8h9i0j1k2l",
    "ApiKey": "key_live_xxxxxxxxxxxxxxxxxxxxxxxxxxxx",
    "ChecksumKey": "checksum_xxxxxxxxxxxxxxxxxxxxxxxxxxxx",
    "BaseUrl": "https://api-sandbox.payos.vn",
    "IsProduction": false
  }
}
```

**Save file!** ✅

---

## Bước 3️⃣: Register Webhook ở PayOS Dashboard (2 phút)

### 3a. Vào Webhooks Settings

```
Dashboard → Settings (⚙️) → Webhooks
```

### 3b. Thêm Webhook URL Mới

Nếu chưa có webhook, click **"Add Webhook"** hoặc **"Create"**

### 3c. Paste Webhook URL

**Paste URL này:**
```
https://4302-2001-ee0-50db-f6f0-f452-2c48-8a4d-e949.ngrok-free.app/api/v1/billing/payos/webhook
```

### 3d. Save / Confirm

- Status phải là ✅ **Active** 
- Lưu lại

---

## Bước 4️⃣: Khởi động Services (5 phút)

### Terminal 1️⃣ - Ngrok (Giữ mở)
```powershell
ngrok http 8080
```

**Giữ cửa sổ này mở suốt!** ✅

### Terminal 2️⃣ - Billing Service

```powershell
cd d:\WarpTalk\warptalk-backend\billing

dotnet run --project src/WarpTalk.BillingService.API -c Debug
```

Chờ tới khi thấy:
```
info: WarpTalk.BillingService.API[0]
      Now listening on: https://localhost:5445
      Now listening on: http://localhost:8080
info: PayOS Service initialized
```

✅ OK! Billing Service đã chạy

---

## Bước 5️⃣: Test Payment Link (Postman)

### 5a. Import Postman Collection

1. Mở **Postman**
2. Click **File → Import**
3. Chọn file: `d:\WarpTalk\warptalk-backend\billing\postman\WarpTalk.Billing.PayOS.Sandbox.postman_collection.json`
4. Click **Import**

### 5b. Update Variables

1. Vào **Collections** → **WarpTalk Billing PayOS Sandbox**
2. Tab **Variables**
3. Sửa `base_url`:
   ```
   Current value: https://4302-2001-ee0-50db-f6f0-f452-2c48-8a4d-e949.ngrok-free.app
   ```
4. **Save**

### 5c. Test Endpoint: Create Checkout

1. Vào tab **Requests**
2. Chọn: **1. Create Payment Link (Plan Upgrade)**
3. Click **Send**

**Response mong đợi:**
```json
{
  "code": "00",
  "message": "Success",
  "data": {
    "checkoutUrl": "https://pay.payos.vn/web/...",
    "orderCode": 2603300123456,
    "qrCode": "..."
  }
}
```

✅ OK! API hoạt động

---

## Bước 6️⃣: Hoàn thành Payment (3 phút)

### 6a. Copy Checkout URL

Từ response Postman, copy giá trị `checkoutUrl`

Ví dụ:
```
https://pay.payos.vn/web/c79a5a3a-e0df-46d8-9c8e-b4b9a8d4e5f6
```

### 6b. Mở trong Browser

Dán URL vào browser, bấm Enter

### 6c. Nhập Thẻ Test

```
Card Number:  4111 1111 1111 1111
Expiry:       12/25
CVV:          123
```

Click **Pay** / **Thanh toán**

### 6d. Xác nhận OTP

```
OTP: 123456
```

Nhập rồi click **Confirm**

### 6e. Chờ Webhook

Chờ khoảng **3-5 giây** để PayOS gửi webhook tới Billing Service

---

## ✅ Bước 7️⃣: Kiểm tra Kết quả

### Check 1: Logs

**Ở Terminal Billing Service, tìm dòng:**
```
info: Webhook received for order 2603300123456
info: Processing webhook: COMPLETED
info: Payment successful, quota topped up
```

✅ Webhook được nhận!

### Check 2: Database

**Kết nối Postgres:**
```sql
SELECT * FROM transactions 
WHERE order_code = 2603300123456;
```

**Kết quả:**
| Column | Value |
|--------|-------|
| order_code | 2603300123456 |
| workspace_id | 77777777-7777-7777-7777-777777777777 |
| amount | 199000 |
| status | SUCCESS |
| completed_at | 2026-04-30 15:30:45 |

✅ Transaction lưu thành công!

### Check 3: Quota

```sql
SELECT * FROM usage_quotas 
WHERE workspace_id = '77777777-7777-7777-7777-777777777777';
```

**Quota balance sẽ tăng thêm** ✅

---

## 🔥 Nếu Gặp Lỗi

### ❌ "Connection refused" / "Connection timeout"

```
✓ Ngrok terminal vẫn chạy không?
✓ Billing Service vẫn chạy không?
✓ Port 8080 bị chiếm?
✓ Firewall chặn Ngrok?
```

**Fix:** Khởi động lại ngrok & Billing Service

### ❌ "Invalid Signature"

```
✓ Checksum Key đúng không?
✓ Copy từ PayOS Dashboard lại
✓ Không có khoảng trắng thừa
```

**Fix:** Cập nhật lại appsettings.Development.json

### ❌ "Webhook not received"

```
✓ Webhook URL ở PayOS Dashboard có đúng không?
✓ Ngrok URL chưa thay đổi?
✓ Billings Service có error không?
```

**Fix:** 
1. Check Webhook URL ở PayOS Dashboard
2. Restart ngrok & Billing Service

### ❌ "Order not found"

```
✓ Order code phải unique trong 24 giờ
✓ Dùng order code từ response Postman
```

**Fix:** Chờ 24 giờ hoặc dùng order code khác

---

## 📋 Checklist Hoàn thành

- [ ] PayOS Dashboard truy cập được
- [ ] Lấy được 3 credentials (Client ID, API Key, Checksum Key)
- [ ] Cấu hình `appsettings.Development.json` đã điền đủ
- [ ] Webhook URL registered ở PayOS Dashboard
- [ ] Ngrok chạy trên port 8080
- [ ] Billing Service chạy trên port 5445 & 8080
- [ ] Postman collection imported
- [ ] `base_url` variable updated
- [ ] Create Payment Link endpoint return checkout URL
- [ ] Payment completed với thẻ test
- [ ] Webhook received (check logs)
- [ ] Database transaction updated
- [ ] Quota increased

---

## 🎉 Khi mọi thứ OK

Bạn đã hoàn thành:
1. ✅ PayOS Sandbox integration
2. ✅ Local webhook via Ngrok
3. ✅ Real payment flow (test mode)
4. ✅ Database integration
5. ✅ Ready for staging/production

**Bước tiếp theo: Frontend integration!** 🚀

---

**Ngrok URL Của Bạn:**
```
https://4302-2001-ee0-50db-f6f0-f452-2c48-8a4d-e949.ngrok-free.app
```

**Webhook URL:**
```
https://4302-2001-ee0-50db-f6f0-f452-2c48-8a4d-e949.ngrok-free.app/api/v1/billing/payos/webhook
```

Lưu lại để sử dụng! 📌
