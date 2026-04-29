# Feature Specification: Dependency Map (WT-09)

**Feature Branch**: `feature/wt-9-xay-dung-billing-quota-service-thanh-toan-va-quan-ly-dung`  
**Created**: 2026-04-29  
**Status**: Draft  
**Input**: Issue WT-09 – Phase 1 Discovery  

---

## 1. Service Dependency Overview

Sơ đồ dưới đây mô tả toàn bộ phụ thuộc (upstream/downstream) của **Billing & Quota Service** với các service khác trong hệ thống WarpTalk.

```
                          ┌─────────────────────────────┐
                          │      External Payment        │
                          │       PayOS Gateway          │
                          │  (POST /v2/payment-requests) │
                          └────────────┬────────────────┘
                                       │ Webhook (HTTPS POST)
                                       ▼
┌──────────────┐   REST (quota/check)  ┌──────────────────────────┐
│   API Gateway │──────────────────────▶│                          │
│  (WT-Gateway) │◀──────────────────────│   Billing & Quota        │
│               │   403 / 200 response  │       Service            │
└──────────────┘                       │    (WarpTalk.Billing)     │
                                       │                          │
┌──────────────┐   REST (quota/deduct) │                          │
│  AI Worker   │──────────────────────▶│                          │
│  (STT/TTS/   │◀──────────────────────│                          │
│  Translate)  │   200 / 402 response  │                          │
└──────────────┘                       │                          │
                                       │                          │
┌──────────────┐   Event (pub/sub)     │                          │
│  Notify Svc  │◀──────────────────────│                          │
│   (WT-38)    │   Quota_Warning       │                          │
│              │   Quota_Exhausted     └──────────────────────────┘
└──────────────┘
```

---

## 2. Dependency Detail Table

| Dependency | Direction | Protocol | Endpoint / Topic | Trigger Condition | SLA Expectation |
|---|---|---|---|---|---|
| **PayOS** (External) | Inbound | HTTPS Webhook | `POST /api/v1/billing/payos/webhook` | Khi người dùng hoàn tất thanh toán QR | Phải xử lý trong < 5s, trả 200 OK |
| **PayOS** (External) | Outbound | HTTPS REST | `POST /v2/payment-requests` | Host khởi tạo checkout | Timeout 10s, retry 3 lần |
| **PayOS** (External) | Outbound | HTTPS REST | `GET /v2/payment-requests/{orderCode}` | Cronjob polling PENDING > 30 phút | Mỗi 15 phút, batch size ≤ 50 |
| **API Gateway** (WT-Gateway) | Inbound | REST | `GET /api/v1/billing/quota/check` | Trước khi cho Host khởi động AI | P99 < 50ms – Critical path |
| **AI Worker** | Inbound | REST | `POST /api/v1/billing/quota/deduct` | Sau mỗi phiên xử lý AI | P99 < 100ms, idempotency required |
| **AI Worker** | Inbound | REST | `POST /api/v1/billing/quota/refund` | Worker crash hoặc zero-output | Idempotency required |
| **Notify Service** (WT-38) | Outbound | Event (MQ/gRPC) | Topic: `billing.quota.events` | Quota đạt 80%, 95%, 100% | Fire-and-forget, at-least-once |
| **User Service** | Outbound | gRPC | `UserService.GetUser(userId)` | Khi cần validate Host ownership | P99 < 30ms |
| **PostgreSQL** (Primary DB) | Outbound | TCP | Internal connection pool | Mọi read/write transaction | Connection pool 20–50, timeout 30s |
| **Redis** (Cache) | Outbound | TCP | Key: `quota:{workspaceId}` | Cache quota cho quota/check hot path | TTL 30s, fallback to DB |

---

## 3. Interface Contracts Per Dependency

### 3.1. API Gateway → Billing (quota/check)

**Direction**: Gateway gọi Billing trước khi cho phép Host kích hoạt AI.

```
Request:
  GET /api/v1/billing/quota/check
  Headers:
    X-Host-Id: {userId}
    X-Workspace-Id: {workspaceId}

Response 200 OK:
  {
    "hasQuota": true,
    "remainingMinutes": 124.5,
    "planType": "Pro",
    "featureFlags": {
      "advancedTranslation": true,
      "premiumVoice": true,
      "maxParticipants": 25
    }
  }

Response 403 Forbidden:
  {
    "hasQuota": false,
    "reason": "QUOTA_EXHAUSTED",
    "remainingMinutes": 0
  }
```

### 3.2. AI Worker → Billing (quota/deduct)

**Direction**: Worker gọi sau khi hoàn thành một segment xử lý.

```
Request:
  POST /api/v1/billing/quota/deduct
  Headers:
    Idempotency-Key: Deduct_{sessionId}_{turnId}
    X-Worker-Id: {workerId}
  Body:
  {
    "hostId": "user-uuid",
    "workspaceId": "ws-uuid",
    "sessionId": "session-uuid",
    "consumedMinutes": 0.5,
    "source": "STT" | "TTS" | "Translation"
  }

Response 200 OK: { "success": true, "remainingMinutes": 124.0 }
Response 402 Payment Required: { "success": false, "reason": "QUOTA_EXHAUSTED" }
Response 409 Conflict (duplicate): { "success": true, "idempotent": true }
```

### 3.3. Billing → Notify Service (Event)

**Direction**: Billing publish event, Notify Service consume.

> Xem chi tiết schema event tại: `BillingEventPayloadSchema.md`

| Event | Topic | Trigger |
|---|---|---|
| `Quota_Warning` | `billing.quota.events` | Tiêu thụ vượt 80% hoặc 95% |
| `Quota_Exhausted` | `billing.quota.events` | Quota chạm 0 |

---

## 4. Dependency Risk & Fallback

| Dependency | Risk | Fallback Strategy |
|---|---|---|
| PayOS Webhook miss | Thanh toán thành công nhưng quota không được cộng | Cronjob polling mỗi 15 phút (Safety Net) |
| Notify Service down | Host không nhận cảnh báo quota | Log event vào DB `pending_notifications`, retry sau khi Notify recover |
| Redis down | Quota check chậm hơn | Fallback đọc thẳng từ PostgreSQL, alert ops team |
| AI Worker retry storm | Nhiều deduct request trùng lặp | Idempotency key chặn tại DB layer |
| User Service timeout | Không validate được Host | Fail-open với cached token claims (JWT), log warning |

---

## 5. Reviewer

| Role | Name | Status |
|---|---|---|
| Feature Owner | TBD | Pending |
| Security Review | TBD | Pending |
| Architecture Review | TBD | Pending |
