---
description: "Task list for Notification Inbox Persistence implementation"
---

# Tasks: Notification Inbox Persistence

**Input**: Design documents from `/specs/001-notification-inbox-persistence/`
**Prerequisites**: plan.md (required), spec.md (required)

## Format: `[ID] [P?] [Story] Description`

## Phase 1: Setup, Domain Model & Data Security (WT-7 Phase 1)

**Purpose**: Database schema expansion & encryption preparation

- [ ] T001 [P] Create `NotificationMessage` entity in `NotificationService.Domain`.
- [ ] T002 Update `NotificationDbContext` to include `DbSet<NotificationMessage>` with mapping to `notification_messages` schema.
- [ ] T003 Add database indexes for `user_id`, `is_read`, and `created_at`.
- [ ] T004 Review SQL Server DB configuration for Encryption at Rest policies (TDE, etc.).
- [ ] T005 Define payload boundary: Ensure `PayloadJson` excludes secret/internal credential dumping.
- [ ] T006 Generate and apply EF Core Migration for the new tables.

---

## Phase 2: Application Service + REST API (WT-7 Phase 2)

**Purpose**: Core CRUD functionality, Access Controls & IDOR checks

- [ ] T007 [P] Create DTOs: `NotificationMessageDto`, `NotificationPaginatedResponse`. Integrate sanitization where applicable to avoid XSS.
- [ ] T008 Update `INotificationService` with methods: `GetNotificationsAsync` (paginated), `MarkAsReadAsync` (verify ownership), `MarkAllAsReadAsync`, and `CreateNotificationAsync`. 
- [ ] T009 Implement REST `NotificationsController`: `GET /api/v1/notifications`, `PATCH /api/v1/notifications/{id}/read`, `PATCH /api/v1/notifications/read-all`.
- [ ] T010 Force API controllers to resolve the `userId` strictly from the Auth Token (HTTP Context).
- [ ] T011 Hook up API Gateway / Middleware Rate Limiting for the Inbox endpoints.
- [ ] T012 [P] Add unit/integration tests confirming IDOR protection (e.g., trying to read another user's message yields 403/404).

---

## Phase 3: Realtime, Integration & Transport Security (WT-7 Phase 3)

**Purpose**: Connecting the persisted state to the realtime flow securely.

- [ ] T013 Update gRPC `SendNotification` to call `CreateNotificationAsync` and persist data before returning/broadcasting. 
- [ ] T014 Ensure gRPC service-to-service links enforce TLS (and setup mTLS if supported by infrastructure).
- [ ] T015 Update Gateway `NotificationHub` so `read` and `read-all` actions call the REST API or gRPC to persist the state locally.
- [ ] T016 Ensure SignalR broadcast still properly triggers to sync cross-tab state after persist succeeds.

---

## Phase 4: Security Verification & Defintion of Done (WT-7 Security Check)

- [ ] T017 Add Postman tests that attempt IDOR (User B acting on User A's unread msg) and ensure failure.
- [ ] T018 Validate payload output does not leak unencoded HTML/JS (stored XSS).
- [ ] T019 Provide evidence of Transport Security (TLS checks).
- [ ] T020 Review retention policies for historical notifications (audit logging if required).
- [ ] T021 End-to-end verification demo logic.
