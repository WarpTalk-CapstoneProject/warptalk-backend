# Feature Specification: Event Payload Schema (WT-09)

**Feature Branch**: `feature/wt-9-xay-dung-billing-quota-service-thanh-toan-va-quan-ly-dung`  
**Created**: 2026-04-29  
**Status**: Draft  
**Input**: Issue WT-09 – Phase 1 Discovery  

---

## 1. Event Bus Overview

Billing Service publish event lên Message Broker (RabbitMQ / hoặc gRPC stream tùy triển khai). Notify Service (WT-38) và AI Gateway subscribe vào topic tương ứng.

- **Topic/Exchange**: `billing.quota.events`  
- **Delivery guarantee**: At-least-once  
- **Consumer**: Notify Service (WT-38), API Gateway (optional subscribe)  
- **Serialization**: JSON (UTF-8)  
- **Envelope version**: `1.0`  

---

## 2. Common Event Envelope

Mọi event đều bọc trong cấu trúc envelope chung trước khi publish:

```json
{
  "eventId": "uuid-v4",
  "eventType": "Quota_Warning | Quota_Exhausted | Quota_Refunded",
  "version": "1.0",
  "occurredAt": "2026-04-29T06:30:00Z",
  "source": "WarpTalk.BillingService",
  "correlationId": "session-uuid hoặc transaction-uuid",
  "payload": { }
}
```

---

## 3. Event: `Quota_Warning`

Kích hoạt khi Host tiêu thụ vượt ngưỡng **80%** (Warning) hoặc **95%** (Critical) tổng quota hiện tại.

```json
{
  "eventId": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
  "eventType": "Quota_Warning",
  "version": "1.0",
  "occurredAt": "2026-04-29T06:30:00Z",
  "source": "WarpTalk.BillingService",
  "correlationId": "session-uuid",
  "payload": {
    "hostId": "user-uuid-host",
    "workspaceId": "ws-uuid",
    "sessionId": "session-uuid",
    "planType": "Pro",
    "severity": "Warning",
    "thresholdPercent": 80,
    "consumedMinutes": 402.5,
    "totalAllocatedMinutes": 500.0,
    "remainingMinutes": 97.5,
    "consumedPercent": 80.5,
    "quotaResetAt": "2026-05-01T00:00:00Z",
    "topUpAvailable": true,
    "message": "Bạn đã dùng hơn 80% dung lượng AI tháng này."
  }
}
```

---

## 4. Event: `Quota_Exhausted`

Kích hoạt khi quota của Host chạm **0** trong khi meeting đang active. Đây là event có độ ưu tiên **Critical**.

```json
{
  "eventId": "b2c3d4e5-f6a7-8901-bcde-f12345678901",
  "eventType": "Quota_Exhausted",
  "version": "1.0",
  "occurredAt": "2026-04-29T07:15:00Z",
  "source": "WarpTalk.BillingService",
  "correlationId": "session-uuid",
  "payload": {
    "hostId": "user-uuid-host",
    "workspaceId": "ws-uuid",
    "sessionId": "session-uuid",
    "planType": "Free",
    "exhaustedAt": "2026-04-29T07:15:00Z",
    "totalAllocatedMinutes": 30.0,
    "consumedMinutes": 30.0,
    "remainingMinutes": 0.0,
    "gracePeriodMinutes": 1.0,
    "gracePeriodExpiresAt": "2026-04-29T07:16:00Z",
    "aiShutdownRequired": true,
    "affectedParticipants": 4,
    "topUpAvailable": true,
    "upgradeUrl": "https://warptalk.io/billing/upgrade",
    "errorCode": "QUOTA_EXCEEDED_TERMINATED",
    "message": "Dung lượng AI của phòng này đã hết."
  }
}
```

---

## 5. Event: `Quota_Refunded`

Kích hoạt khi Admin hoặc System hoàn trả quota cho Host.

```json
{
  "eventId": "c3d4e5f6-a7b8-9012-cdef-123456789012",
  "eventType": "Quota_Refunded",
  "version": "1.0",
  "occurredAt": "2026-04-29T08:00:00Z",
  "source": "WarpTalk.BillingService",
  "correlationId": "refund-admin-ticket-001",
  "payload": {
    "hostId": "user-uuid-host",
    "workspaceId": "ws-uuid",
    "refundedMinutes": 15.0,
    "reason": "WORKER_CRASH",
    "initiatedBy": "system",
    "newRemainingMinutes": 15.0,
    "refundTransactionId": "refund-uuid",
    "message": "15 phút AI đã được hoàn trả."
  }
}
```
