# Feature Specification: API Contract (WT-09)

**Feature Branch**: `feature/wt-9-xay-dung-billing-quota-service-thanh-toan-va-quan-ly-dung`  
**Created**: 2026-04-29  
**Status**: Draft  
**Input**: Issue WT-09 – Phase 1 Discovery  

---

## 1. REST API Overview

Billing & Quota Service cung cấp 4 nhóm API chính:
1. **Quota Management** (Internal): Phục vụ Gateway và AI Worker kiểm tra, tiêu hao và hoàn trả quota.
2. **Payment Integration** (External/Public): Nhận Webhook từ PayOS.
3. **Checkout** (Client): Phục vụ Web App tạo link thanh toán.
4. **Subscription** (Client): Phục vụ hiển thị thông tin gói cước.

*Base Path: `/api/v1/billing`*

---

## 2. Quota Management APIs (Internal / Gateway / Worker)

Các API này không gọi trực tiếp từ Client mà thông qua API Gateway hoặc từ internal AI Worker. Yêu cầu bảo mật bằng Server-to-Server token/MTLS hoặc Private Subnet.

### 2.1. Kiểm tra trạng thái Quota (Check)
- **Endpoint**: `GET /quota/check`
- **Caller**: API Gateway (trước khi start session)
- **Headers**:
  - `X-Workspace-Id`: UUID
- **Response (200 OK)**:
```json
{
  "hasQuota": true,
  "planId": "uuid",
  "planName": "Pro",
  "remainingMinutes": 124.5,
  "maxParticipants": 25,
  "features": { "advancedTranslation": true }
}
```
- **Response (403 Forbidden)**: Hết hạn ngạch.

### 2.2. Trừ Quota (Deduct)
- **Endpoint**: `POST /quota/deduct`
- **Caller**: AI Worker (thường xuyên)
- **Headers**:
  - `Idempotency-Key`: `Deduct_{SessionId}_{TurnId}`
- **Request Body**:
```json
{
  "workspaceId": "uuid",
  "sessionId": "uuid",
  "consumedMinutes": 0.5,
  "source": "STT"
}
```
- **Response (200 OK)**: `{"success": true, "remainingMinutes": 124.0}`
- **Response (402 Payment Required)**: Hết hạn ngạch.
- **Response (409 Conflict)**: Bị chặn bởi Idempotency.

### 2.3. Hoàn trả Quota (Refund)
- **Endpoint**: `POST /quota/refund`
- **Caller**: Fallback Worker / Admin Tool
- **Headers**:
  - `Idempotency-Key`: `Refund_{SessionId}_{TurnId}`
- **Request Body**:
```json
{
  "workspaceId": "uuid",
  "sessionId": "uuid",
  "refundedMinutes": 0.5,
  "reason": "WORKER_CRASH"
}
```
- **Response (200 OK)**: `{"success": true, "remainingMinutes": 124.5}`

---

## 3. Payment Integration APIs

### 3.1. Nhận Webhook từ PayOS
- **Endpoint**: `POST /payos/webhook`
- **Caller**: PayOS Server
- **Headers**: Không yêu cầu Auth, dựa vào xác thực Data Signature bên trong payload.
- **Request Body**: (Theo format chuẩn của PayOS)
```json
{
  "code": "00",
  "desc": "success",
  "data": {
    "orderCode": 123456789,
    "amount": 200000,
    "description": "Thanh toan WarpTalk",
    "accountNumber": "123456",
    "reference": "REF123",
    "transactionDateTime": "2026-04-29 12:00:00",
    "currency": "VND",
    "paymentLinkId": "link-uuid",
    "code": "00",
    "desc": "success",
    "counterAccountBankId": "970436",
    "counterAccountName": "NGUYEN VAN A",
    "counterAccountNumber": "987654321",
    "counterAccountBankName": "Vietcombank"
  },
  "signature": "hmac-sha256-hash-string"
}
```
- **Response (200 OK)**: `{"success": true}` (Kể cả khi bị lặp webhook).
- **Response (400 Bad Request)**: Lỗi sai Signature.

---

## 4. Checkout & Subscription APIs (Client-facing)

### 4.1. Tạo Link Thanh Toán
- **Endpoint**: `POST /checkout/create-link`
- **Caller**: Front-end Web App
- **Headers**: Authorization Bearer Token
- **Request Body**:
```json
{
  "planId": "uuid", // Nếu mua base plan
  "topUpMinutes": 100 // Nếu mua thêm quota
}
```
- **Response (200 OK)**:
```json
{
  "orderCode": 123456789,
  "checkoutUrl": "https://pay.payos.vn/web/xxxx",
  "amountVnd": 200000
}
```

### 4.2. Lấy danh sách Plan
- **Endpoint**: `GET /plans`
- **Caller**: Front-end Web App
- **Response (200 OK)**:
```json
[
  {
    "id": "uuid",
    "name": "Free",
    "priceVnd": 0,
    "baseQuotaMinutes": 30,
    "maxParticipants": 5
  },
  {
    "id": "uuid",
    "name": "Pro",
    "priceVnd": 199000,
    "baseQuotaMinutes": 500,
    "maxParticipants": 25
  }
]
```
