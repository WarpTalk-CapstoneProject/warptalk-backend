# 🧪 Swagger Test Guide - Billing Service

> Hướng dẫn test toàn bộ API endpoints theo chuẩn: Happy Path ✅ | Negative ❌ | Edge Case ⚠️ | Integration 🔁

**Chuẩn bị trước khi test:**
- Mở Swagger UI: `https://localhost:5001/swagger`
- Copy UUID từ database seed hoặc tạo UUID mới
- Chuẩn bị test data (workspace, plan, user)

---

## 📋 Table of Contents

1. [AdminBilling - Transactions](#1-adminbilling---transactions)
2. [AdminBilling - Subscription](#2-adminbilling---subscription)
3. [Checkout](#3-checkout)
4. [Quota Management](#4-quota-management)
5. [Transaction - Payment Link](#5-transaction---payment-link)
6. [Webhook PayOS](#6-webhook-payos)
7. [Usage Events](#7-usage-events)
8. [Health Check](#8-health-check)
9. [Integration Flow](#9-integration-flow---full-payment-cycle)

---

## 1. AdminBilling - Transactions

### ✅ Happy Path: Lấy danh sách transactions

**Endpoint:** `GET /api/admin/transactions/{workspaceId}`

**Parameters:**
```
workspaceId: 550e8400-e29b-41d4-a716-446655440000  (UUID hợp lệ có data)
page: 1
pageSize: 20
```

**Expected Response:** `200 OK`
```json
{
  "data": [
    {
      "id": "uuid",
      "workspaceId": "550e8400-e29b-41d4-a716-446655440000",
      "amount": 200000,
      "status": "SUCCESS",
      "type": "SUBSCRIPTION",
      "createdAt": "2026-05-04T12:33:10Z"
    }
  ],
  "pageInfo": {
    "page": 1,
    "pageSize": 20,
    "totalCount": 100
  }
}
```

**Steps in Swagger:**
1. Click "GET /api/admin/transactions/{workspaceId}"
2. Nhập `workspaceId` (copy từ database)
3. Set `page=1`, `pageSize=20`
4. Click **"Try it out"** → **"Execute"**
5. ✅ Verify status 200 OK và data returned

---

### ❌ Negative: Invalid UUID Format

**Endpoint:** `GET /api/admin/transactions/{workspaceId}`

**Parameters:**
```
workspaceId: abc123-invalid  (format sai)
page: 1
pageSize: 20
```

**Expected Response:** `400 Bad Request`
```json
{
  "code": "INVALID_FORMAT",
  "message": "WorkspaceId must be a valid UUID"
}
```

---

### ❌ Negative: Workspace Not Found

**Endpoint:** `GET /api/admin/transactions/{workspaceId}`

**Parameters:**
```
workspaceId: 550e8400-e29b-41d4-a716-000000000000  (UUID đúng format nhưng không tồn tại)
page: 1
pageSize: 20
```

**Expected Response:** `404 Not Found` hoặc `200 OK` với empty list
```json
{
  "data": [],
  "pageInfo": {
    "page": 1,
    "pageSize": 20,
    "totalCount": 0
  }
}
```

---

### ⚠️ Edge Case: Invalid Pagination

**Test 1: page = 0**
```
workspaceId: 550e8400-e29b-41d4-a716-446655440000
page: 0
pageSize: 20
```
**Expected:** `400 Bad Request` - "Page must be >= 1"

**Test 2: pageSize = 1000**
```
workspaceId: 550e8400-e29b-41d4-a716-446655440000
page: 1
pageSize: 1000
```
**Expected:** `400 Bad Request` - "PageSize must be <= 100" (hoặc auto limit to 100)

---

## 2. AdminBilling - Subscription

### ✅ Happy Path: Create Subscription

**Endpoint:** `POST /api/admin/subscription/create`

**Request Body:**
```json
{
  "workspaceId": "550e8400-e29b-41d4-a716-446655440000",
  "planId": "22222222-2222-2222-2222-222222222222",
  "ownerUserId": "770e8400-e29b-41d4-a716-446655440000",
  "startDate": "2026-05-04T12:00:00Z",
  "durationDays": 30
}
```

**Expected Response:** `201 Created`
```json
{
  "id": "subscription-uuid",
  "workspaceId": "550e8400-e29b-41d4-a716-446655440000",
  "planId": "22222222-2222-2222-2222-222222222222",
  "status": "ACTIVE",
  "startDate": "2026-05-04T12:00:00Z",
  "endDate": "2026-06-03T12:00:00Z",
  "createdAt": "2026-05-04T12:33:10Z"
}
```

**Steps in Swagger:**
1. Click "POST /api/admin/subscription/create"
2. Paste JSON request body
3. Click "Try it out" → "Execute"
4. ✅ Verify 201 Created with subscription ID

---

### ❌ Negative: Missing Required Field

**Request Body** (thiếu `planId`):
```json
{
  "workspaceId": "550e8400-e29b-41d4-a716-446655440000",
  "ownerUserId": "770e8400-e29b-41d4-a716-446655440000",
  "startDate": "2026-05-04T12:00:00Z",
  "durationDays": 30
}
```

**Expected Response:** `400 Bad Request`
```json
{
  "code": "VALIDATION_ERROR",
  "message": "planId is required",
  "errors": {
    "planId": ["The planId field is required"]
  }
}
```

---

### ❌ Negative: Invalid Duration

**Request Body** (`durationDays` âm):
```json
{
  "workspaceId": "550e8400-e29b-41d4-a716-446655440000",
  "planId": "22222222-2222-2222-2222-222222222222",
  "ownerUserId": "770e8400-e29b-41d4-a716-446655440000",
  "startDate": "2026-05-04T12:00:00Z",
  "durationDays": -10
}
```

**Expected Response:** `400 Bad Request`
```json
{
  "code": "VALIDATION_ERROR",
  "message": "durationDays must be > 0",
  "errors": {
    "durationDays": ["DurationDays must be greater than 0"]
  }
}
```

---

### ⚠️ Edge Case: Zero Duration

**Request Body:**
```json
{
  "workspaceId": "550e8400-e29b-41d4-a716-446655440000",
  "planId": "660e8400-e29b-41d4-a716-446655440000",
  "ownerUserId": "770e8400-e29b-41d4-a716-446655440000",
  "startDate": "2026-05-04T12:00:00Z",
  "durationDays": 0
}
```

**Expected Response:** `400 Bad Request` - "DurationDays must be >= 1"

---

### ⚠️ Edge Case: Past Start Date

**Request Body:**
```json
{
  "workspaceId": "550e8400-e29b-41d4-a716-446655440000",
  "planId": "660e8400-e29b-41d4-a716-446655440000",
  "ownerUserId": "770e8400-e29b-41d4-a716-446655440000",
  "startDate": "2020-05-04T12:00:00Z",
  "durationDays": 30
}
```

**Expected:** Hệ thống accept (có thể dùng cho back-date) hoặc reject với: `400 Bad Request` - "StartDate cannot be in the past"

**→ Document business rule của bạn**

---

### ✅ Upgrade Subscription

**Endpoint:** `PUT /api/admin/subscription/{subscriptionId}/upgrade`

**Parameters:**
```
subscriptionId: subscription-uuid (hợp lệ)
```

**Request Body:**
```json
{
  "newPlanId": "880e8400-e29b-41d4-a716-446655440000",
  "extendDays": 0
}
```

**Expected Response:** `200 OK`
```json
{
  "id": "subscription-uuid",
  "planId": "880e8400-e29b-41d4-a716-446655440000",
  "status": "ACTIVE",
  "endDate": "2026-06-03T12:00:00Z",
  "upgradedAt": "2026-05-04T12:33:10Z"
}
```

---

### ❌ Upgrade Non-existent Subscription

**Parameters:**
```
subscriptionId: 000e8400-e29b-41d4-a716-000000000000  (không tồn tại)
```

**Expected Response:** `404 Not Found`
```json
{
  "code": "NOT_FOUND",
  "message": "Subscription not found"
}
```

---

### ✅ Cancel Subscription

**Endpoint:** `DELETE /api/admin/subscription/{subscriptionId}`

**Parameters:**
```
subscriptionId: subscription-uuid (hợp lệ, đang active)
```

**Expected Response:** `200 OK` hoặc `204 No Content`
```json
{
  "id": "subscription-uuid",
  "status": "CANCELLED",
  "cancelledAt": "2026-05-04T12:33:10Z"
}
```

---

### ❌ Cancel with Null ID

**Endpoint:** `DELETE /api/admin/subscription/{subscriptionId}`

**Parameters:**
```
subscriptionId: (để trống)
```

**Expected Response:** `400 Bad Request`
```json
{
  "code": "VALIDATION_ERROR",
  "message": "subscriptionId is required"
}
```

---

### 🔁 Edge Case: Cancel Twice (Idempotency)

**First Cancel:**
```
subscriptionId: subscription-uuid
```
**Response:** `200 OK` - Status: CANCELLED

**Second Cancel (cùng subscription):**
```
subscriptionId: subscription-uuid
```

**Expected Behavior:**
- **Option 1 (Idempotent):** `200 OK` - Return same status
- **Option 2 (Strict):** `400 Bad Request` - "Cannot cancel already cancelled subscription"

**→ Document expected behavior**

---

## 3. Checkout

### ✅ Happy Path: Create Checkout

**Endpoint:** `POST /api/checkout`

**Headers:**
```
X-Workspace-Id: 550e8400-e29b-41d4-a716-446655440000
Content-Type: application/json
```

**Request Body:**
```json
{
  "planId": "660e8400-e29b-41d4-a716-446655440000",
  "topUpMinutes": 10000
}
```

**Expected Response:** `200 OK`
```json
{
  "checkoutId": "checkout-uuid",
  "paymentLink": "https://payos.vn/checkout/link123",
  "orderCode": "1234567890123",
  "amount": 200000,
  "currency": "VND",
  "status": "PENDING",
  "expiresAt": "2026-05-05T12:33:10Z"
}
```

**Steps in Swagger:**
1. Scroll to "POST /api/checkout"
2. Click "Try it out"
3. Thêm header: `X-Workspace-Id: [workspace-uuid]`
4. Paste JSON body
5. Click "Execute"
6. ✅ Verify 200 OK + payment link

---

### ❌ Negative: Missing Header

**Headers:** (không có X-Workspace-Id)
```
Content-Type: application/json
```

**Expected Response:** `401 Unauthorized` hoặc `400 Bad Request`
```json
{
  "code": "MISSING_HEADER",
  "message": "X-Workspace-Id header is required"
}
```

---

### ❌ Negative: Invalid Plan

**Headers:**
```
X-Workspace-Id: 550e8400-e29b-41d4-a716-446655440000
```

**Request Body:**
```json
{
  "planId": "000e8400-e29b-41d4-a716-000000000000",
  "topUpMinutes": 10000
}
```

**Expected Response:** `400 Bad Request` hoặc `404 Not Found`
```json
{
  "code": "PLAN_NOT_FOUND",
  "message": "Plan not found"
}
```

---

### ⚠️ Edge Case: Zero Top-up Minutes

**Request Body:**
```json
{
  "planId": "660e8400-e29b-41d4-a716-446655440000",
  "topUpMinutes": 0
}
```

**Expected Response:** `400 Bad Request`
```json
{
  "code": "VALIDATION_ERROR",
  "message": "topUpMinutes must be > 0"
}
```

---

### ⚠️ Edge Case: Excessive Top-up

**Request Body:**
```json
{
  "planId": "660e8400-e29b-41d4-a716-446655440000",
  "topUpMinutes": 1000000
}
```

**Expected Response:**
- **If limit enforced:** `400 Bad Request` - "topUpMinutes exceeds maximum (100000)"
- **If no limit:** `200 OK` - Create checkout (document your limit)

---

## 4. Quota Management

### ✅ Happy Path: Top-up Quota

**Endpoint:** `POST /api/quota/topup`

**Headers:**
```
X-Workspace-Id: 550e8400-e29b-41d4-a716-446655440000
Content-Type: application/json
```

**Request Body:**
```json
{
  "minutes": 10000
}
```

**Expected Response:** `200 OK`
```json
{
  "workspaceId": "550e8400-e29b-41d4-a716-446655440000",
  "currentQuota": 25000,
  "topUpAmount": 10000,
  "newQuota": 35000,
  "updatedAt": "2026-05-04T12:33:10Z"
}
```

---

### ❌ Negative: Negative Minutes

**Request Body:**
```json
{
  "minutes": -1000
}
```

**Expected Response:** `400 Bad Request`
```json
{
  "code": "VALIDATION_ERROR",
  "message": "minutes must be > 0"
}
```

---

### ❌ Negative: Invalid Data Type

**Request Body:**
```json
{
  "minutes": "abc"
}
```

**Expected Response:** `400 Bad Request`
```json
{
  "code": "VALIDATION_ERROR",
  "message": "minutes must be a number"
}
```

---

### ⚠️ Edge Case: Zero Minutes

**Request Body:**
```json
{
  "minutes": 0
}
```

**Expected Response:** `400 Bad Request` - "minutes must be > 0"

---

### ✅ Upgrade Quota by Plan

**Endpoint:** `POST /api/quota/upgrade-by-plan`

**Headers:**
```
X-Workspace-Id: 550e8400-e29b-41d4-a716-446655440000
```

**Request Body:**
```json
{
  "planId": "660e8400-e29b-41d4-a716-446655440000"
}
```

**Expected Response:** `200 OK`
```json
{
  "workspaceId": "550e8400-e29b-41d4-a716-446655440000",
  "oldQuota": 10000,
  "planQuota": 50000,
  "newQuota": 50000,
  "upgradedAt": "2026-05-04T12:33:10Z"
}
```

---

## 5. Transaction - Payment Link

### ✅ Happy Path: Create Payment Link

**Endpoint:** `POST /api/transaction/create-link`

**Headers:**
```
X-Workspace-Id: 550e8400-e29b-41d4-a716-446655440000
```

**Request Body:**
```json
{
  "planId": "660e8400-e29b-41d4-a716-446655440000",
  "topUpMinutes": 10000
}
```

**Expected Response:** `200 OK`
```json
{
  "orderCode": 1234567890123,
  "paymentLink": "https://payos.vn/checkout/link123",
  "amount": 200000,
  "currency": "VND",
  "status": "PENDING",
  "expiresAt": "2026-05-05T12:33:10Z"
}
```

---

### ❌ Negative: Null Plan ID

**Request Body:**
```json
{
  "planId": null,
  "topUpMinutes": 10000
}
```

**Expected Response:** `400 Bad Request`
```json
{
  "code": "VALIDATION_ERROR",
  "message": "planId is required"
}
```

---

### 🔁 Edge Case: Duplicate Request (Idempotency)

**First Request:**
```json
{
  "planId": "660e8400-e29b-41d4-a716-446655440000",
  "topUpMinutes": 10000
}
```
**Response:** `200 OK` - orderCode: 1234567890123

**Second Request (same data):**
```json
{
  "planId": "660e8400-e29b-41d4-a716-446655440000",
  "topUpMinutes": 10000
}
```

**Expected Behavior:**
- **Option 1 (Idempotent):** `200 OK` - Return same orderCode
- **Option 2 (New):** `200 OK` - Create new order

**→ Document expected behavior** (normally should be idempotent with request tracing)

---

## 6. Webhook PayOS - QUAN TRỌNG NHẤT 🔴

### ✅ Happy Path: Valid Webhook

**Endpoint:** `POST /api/webhook/payos`

**Headers:**
```
Content-Type: application/json
X-PayOS-Signature: VALID_SIGNATURE_FROM_PAYOS
```

**Request Body:**
```json
{
  "code": "00",
  "desc": "success",
  "data": {
    "orderCode": 1234567890123,
    "amount": 200000,
    "description": "WarpTalk Pro Upgrade",
    "accountNumber": "0123456789",
    "reference": "REF-001",
    "transactionDateTime": "2026-05-04 12:33:10",
    "currency": "VND",
    "paymentLinkId": "plink_123",
    "code": "00",
    "desc": "success"
  },
  "signature": "VALID_SIGNATURE"
}
```

**Expected Response:** `200 OK`
```json
{
  "success": true,
  "message": "Webhook processed successfully",
  "orderCode": 1234567890123
}
```

**Verify in Database:**
- ✅ Transaction status = SUCCESS
- ✅ Subscription status = ACTIVE
- ✅ Quota updated
- ✅ Payment recorded

---

### ❌ Negative: Invalid Signature (🔴 SECURITY CRITICAL)

**Request Body:** (same as happy path, but with wrong signature)
```json
{
  "code": "00",
  "desc": "success",
  "data": {...},
  "signature": "WRONG_SIGNATURE"
}
```

**Expected Response:** `401 Unauthorized`
```json
{
  "success": false,
  "code": "INVALID_SIGNATURE",
  "message": "Webhook signature verification failed"
}
```

**Verify:**
- ❌ Transaction NOT created
- ❌ Subscription NOT activated
- ❌ Log security alert

---

### ❌ Negative: Failed Payment (code != "00")

**Request Body:**
```json
{
  "code": "01",
  "desc": "payment failed",
  "data": {
    "orderCode": 1234567890123,
    "amount": 200000,
    "code": "01",
    "desc": "payment failed"
  },
  "signature": "VALID_SIGNATURE"
}
```

**Expected Response:** `200 OK`
```json
{
  "success": true,
  "message": "Webhook processed",
  "orderCode": 1234567890123
}
```

**Verify in Database:**
- ✅ Transaction status = FAILED
- ❌ Subscription NOT activated
- ❌ Quota NOT updated

---

### 🔁 Edge Case: Duplicate Webhook (Idempotency - CRITICAL)

**First Webhook:**
```json
{
  "code": "00",
  "data": {
    "orderCode": 1234567890123,
    "amount": 200000,
    ...
  },
  "signature": "VALID_SIGNATURE"
}
```
**Response:** `200 OK` - Transaction created

**Second Webhook (same orderCode):**
```json
{
  "code": "00",
  "data": {
    "orderCode": 1234567890123,
    "amount": 200000,
    ...
  },
  "signature": "VALID_SIGNATURE"
}
```

**Expected Response:** `200 OK`
```json
{
  "success": true,
  "message": "Webhook processed (already processed)",
  "orderCode": 1234567890123
}
```

**Verify:**
- ✅ No duplicate transaction created
- ✅ Subscription NOT double-activated
- ✅ Quota NOT double-updated

---

## 7. Usage Events

### ✅ Happy Path: Record Usage

**Endpoint:** `POST /api/usage-events`

**Headers:**
```
X-Workspace-Id: 550e8400-e29b-41d4-a716-446655440000
Content-Type: application/json
```

**Request Body:**
```json
{
  "eventType": "token_usage",
  "provider": "openai",
  "usage": {
    "promptTokens": 1200,
    "completionTokens": 800,
    "minutes": 2
  },
  "occurredAt": "2026-05-04T12:33:10Z"
}
```

**Expected Response:** `201 Created`
```json
{
  "eventId": "event-uuid",
  "workspaceId": "550e8400-e29b-41d4-a716-446655440000",
  "eventType": "token_usage",
  "provider": "openai",
  "usage": {
    "promptTokens": 1200,
    "completionTokens": 800,
    "minutes": 2
  },
  "quotaDeducted": 2,
  "recordedAt": "2026-05-04T12:33:10Z"
}
```

**Verify:**
- ✅ Event logged
- ✅ Quota reduced by 2 minutes
- ✅ Usage recorded in analytics

---

### ❌ Negative: Negative Usage

**Request Body:**
```json
{
  "eventType": "token_usage",
  "provider": "openai",
  "usage": {
    "promptTokens": -1000,
    "completionTokens": 800,
    "minutes": 2
  },
  "occurredAt": "2026-05-04T12:33:10Z"
}
```

**Expected Response:** `400 Bad Request`
```json
{
  "code": "VALIDATION_ERROR",
  "message": "Usage values cannot be negative"
}
```

---

### ❌ Negative: Invalid Workspace

**Headers:**
```
X-Workspace-Id: 000e8400-e29b-41d4-a716-000000000000
```

**Expected Response:** `400 Bad Request` hoặc `404 Not Found`
```json
{
  "code": "WORKSPACE_NOT_FOUND",
  "message": "Workspace not found"
}
```

---

### ⚠️ Edge Case: Excessive Usage

**Request Body:**
```json
{
  "eventType": "token_usage",
  "provider": "openai",
  "usage": {
    "promptTokens": 999999999,
    "completionTokens": 999999999,
    "minutes": 99999999
  },
  "occurredAt": "2026-05-04T12:33:10Z"
}
```

**Expected Behavior:**
- **Option 1 (Reject):** `400 Bad Request` - "Usage exceeds maximum"
- **Option 2 (Accept):** `201 Created` - Accept (watch for overflow)
- **Option 3 (Cap):** `201 Created` - Cap at max value

**→ Document your business rule**

---

## 8. Health Check

### ✅ Test Basic Health

**Endpoint:** `GET /health`

**Expected Response:** `200 OK`
```json
{
  "status": "healthy",
  "timestamp": "2026-05-04T12:33:10Z"
}
```

---

### ✅ Test Database Health

**Endpoint:** `GET /health/db`

**Expected Response:** `200 OK` (if DB connected)
```json
{
  "status": "healthy",
  "service": "database",
  "connectionTime": 45
}
```

---

### ✅ Test Readiness

**Endpoint:** `GET /health/ready`

**Expected Response:** `200 OK`
```json
{
  "status": "ready",
  "dependencies": {
    "database": "connected",
    "redis": "connected"
  }
}
```

---

### ❌ Negative: Database Down

**Scenario:** Stop your database service

**Endpoint:** `GET /health/db`

**Expected Response:** `503 Service Unavailable`
```json
{
  "status": "unhealthy",
  "service": "database",
  "error": "Connection timeout"
}
```

---

## 9. Integration Flow - Full Payment Cycle

### 🔁 Complete Flow Test

**Flow:**
1. Create checkout link → Get payment URL
2. Simulate user payment via webhook
3. Verify subscription activated
4. Verify quota updated
5. Record usage events
6. Verify quota deduction

---

### Step 1: Create Checkout

```
POST /api/checkout
X-Workspace-Id: 550e8400-e29b-41d4-a716-446655440000

{
  "planId": "660e8400-e29b-41d4-a716-446655440000",
  "topUpMinutes": 10000
}
```

**✅ Expected:** 200 OK + orderCode: 1234567890123

---

### Step 2: Simulate Payment via Webhook

```
POST /api/webhook/payos
X-PayOS-Signature: VALID_SIGNATURE

{
  "code": "00",
  "desc": "success",
  "data": {
    "orderCode": 1234567890123,
    "amount": 200000,
    "description": "WarpTalk Pro Upgrade",
    "code": "00"
  },
  "signature": "VALID_SIGNATURE"
}
```

**✅ Expected:** 200 OK

---

### Step 3: Verify Subscription Active

```
GET /api/admin/subscription?workspaceId=550e8400-e29b-41d4-a716-446655440000
```

**✅ Expected:**
- Status: ACTIVE
- Recent subscription record

---

### Step 4: Verify Quota Updated

```
GET /api/quota/current
X-Workspace-Id: 550e8400-e29b-41d4-a716-446655440000
```

**✅ Expected:**
- Quota increased by 10000 minutes

---

### Step 5: Record Usage

```
POST /api/usage-events
X-Workspace-Id: 550e8400-e29b-41d4-a716-446655440000

{
  "eventType": "token_usage",
  "provider": "openai",
  "usage": {
    "promptTokens": 500,
    "completionTokens": 300,
    "minutes": 5
  },
  "occurredAt": "2026-05-04T12:33:10Z"
}
```

**✅ Expected:** 201 Created + quotaDeducted: 5

---

### Step 6: Verify Quota Deducted

```
GET /api/quota/current
X-Workspace-Id: 550e8400-e29b-41d4-a716-446655440000
```

**✅ Expected:**
- Quota = (10000 - 5) = 9995 minutes

---

## 📝 Test Execution Checklist

Sử dụng checklist này để track progress:

### Transactions
- [ ] ✅ GET transactions - happy path
- [ ] ❌ GET transactions - invalid UUID
- [ ] ❌ GET transactions - workspace not found
- [ ] ⚠️ GET transactions - invalid pagination

### Subscription
- [ ] ✅ POST create - valid data
- [ ] ❌ POST create - missing field
- [ ] ❌ POST create - invalid duration
- [ ] ⚠️ POST create - zero duration
- [ ] ⚠️ POST create - past date
- [ ] ✅ PUT upgrade - valid ID
- [ ] ❌ PUT upgrade - not found
- [ ] ✅ DELETE cancel - valid ID
- [ ] ❌ DELETE cancel - null ID
- [ ] 🔁 DELETE cancel - idempotency

### Checkout
- [ ] ✅ POST checkout - valid data
- [ ] ❌ POST checkout - missing header
- [ ] ❌ POST checkout - invalid plan
- [ ] ⚠️ POST checkout - zero minutes
- [ ] ⚠️ POST checkout - excessive minutes

### Quota
- [ ] ✅ POST topup - valid data
- [ ] ❌ POST topup - negative minutes
- [ ] ❌ POST topup - invalid type
- [ ] ⚠️ POST topup - zero minutes
- [ ] ✅ POST upgrade-by-plan - valid plan

### Payment Link
- [ ] ✅ POST create-link - valid data
- [ ] ❌ POST create-link - null plan
- [ ] 🔁 POST create-link - duplicate request

### Webhook PayOS
- [ ] ✅ POST webhook - valid signature
- [ ] ❌ POST webhook - invalid signature
- [ ] ❌ POST webhook - failed payment
- [ ] 🔁 POST webhook - duplicate webhook
- [ ] 🔐 Verify transaction created
- [ ] 🔐 Verify subscription activated
- [ ] 🔐 Verify quota updated

### Usage Events
- [ ] ✅ POST usage - valid data
- [ ] ❌ POST usage - negative usage
- [ ] ❌ POST usage - invalid workspace
- [ ] ⚠️ POST usage - excessive usage
- [ ] 🔐 Verify quota deducted

### Health Check
- [ ] ✅ GET /health - 200 OK
- [ ] ✅ GET /health/db - 200 OK
- [ ] ✅ GET /health/ready - 200 OK
- [ ] ❌ GET /health/db - DB down

### Integration
- [ ] 🔁 Full flow: checkout → webhook → subscription active → usage → quota deducted

---

## 🛠️ Tips for Testing

### 1. **Copy Real UUIDs**
```bash
# From database
SELECT TOP 5 id FROM workspaces;
SELECT TOP 5 id FROM plans;
```

### 2. **Generate UUIDs in Swagger**
Use online tools or PowerShell:
```powershell
[guid]::NewGuid().ToString()
```

### 3. **Test Webhook Multiple Times**
Send same webhook 3+ times to verify idempotency

### 4. **Check Logs**
```bash
docker logs billing-api
```

### 5. **Verify Database Changes**
```sql
SELECT * FROM transactions WHERE orderCode = 1234567890123;
SELECT * FROM subscriptions WHERE workspaceId = '550e8400-e29b-41d4-a716-446655440000';
SELECT * FROM quotas WHERE workspaceId = '550e8400-e29b-41d4-a716-446655440000';
```

### 6. **Test with Different Signatures**
```json
{
  "signature": "WRONG_SIG_1234567890"  // Should reject
}
```

### 7. **Measure Webhook Performance**
Track response time in Network tab

### 8. **Test Concurrency**
Send 10 duplicate webhooks in parallel → Verify only 1 transaction created

---

## 📚 Expected API Behavior Summary

| Scenario | Expected | Status |
|----------|----------|--------|
| Create subscription with valid data | 201 Created | ✅ |
| Create subscription without planId | 400 Bad Request | ❌ |
| Upgrade subscription (not found) | 404 Not Found | ❌ |
| Cancel subscription twice | 200 OK (idempotent) | 🔁 |
| Webhook with invalid signature | 401 Unauthorized | 🔴 |
| Webhook with duplicate orderCode | 200 OK (no double update) | 🔁 |
| Usage with negative values | 400 Bad Request | ❌ |
| Zero pageSize | 400 Bad Request | ⚠️ |
| Health check | 200 OK | ✅ |

---

**Last Updated:** May 4, 2026  
**Version:** 1.0  
**Status:** Ready for Testing
