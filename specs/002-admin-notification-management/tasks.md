---
description: "Task list for Admin Notification Management (WT-58)"
---

# Tasks: Admin Notification Management

**Input**: Design documents from `specs/002-admin-notification-management/`
**Prerequisites**: plan.md (required), spec.md (required for user stories)

## Phase 1: Setup & Constants
- [x] T001 Create project structure (Mappers, Helpers, DTOs, Validators) adhering to NFR-ARCH-003/004
- [x] T002 Add `NotificationConstants.cs` defining `NotificationTypes` (PROMOTION, SYSTEM, etc.) and `StatusConstants`
- [x] T003 [Database First] Write a new SQL migration script for `admin_notifications` and synchronize it to infrastructure and agent `init-db.sql` files.
- [ ] T004 [Database First] Run database migration specifically for the new script
- [ ] T005 [Database First] Run `dotnet ef dbcontext scaffold` command to generate the `AdminNotification` entity in the Domain layer

## Phase 2: User Story 1 - Create Notification (P1)
### Tests (Write First!)
- [x] T006 [P] [US1] Unit tests for `CreateAdminNotificationAsync` service method
- [x] T007 [P] [US1] Unit tests for payload schema validation (FluentValidation) per Notification Type
- [x] T008 [P] [US1] Unit tests for target audience deduplication & limit rules (Max 10,000)
- [x] T009 [P] [US1] Integration tests for `POST /api/v1/admin/notifications` rejecting unknown top-level fields

### Implementation
- [x] T010 [US1] Create `IAdminNotificationRepository` and implement it in Infrastructure
- [x] T011 [US1] Implement `NotificationValidationHelper` to handle deduplication and Batch DB queries for target eligibility
- [x] T012 [US1] Implement `AdminNotificationValidator` with Polymorphic payload validation based on `Type`
- [x] T013 [US1] Implement mapping logic to map DTOs to Entities and handle `UpdatedAt`
- [x] T014 [US1] Implement `AdminNotificationService.CreateAsync` invoking IUnitOfWork ONLY (NFR-ARCH-005)
- [x] T015 [US1] Implement chunking strategy for the target audience before passing to delivery trigger
- [x] T016 [US1] Implement `AdminNotificationsController.Create` in API

## Phase 3: User Story 2 & 3 - List and Get Detail (P2)
### Tests (Write First!)
- [x] T017 [P] [US2/3] Unit tests for `GetAdminNotificationsAsync` (pagination, filtering by title/date)
- [x] T018 [P] [US2/3] Unit tests for `GetAdminNotificationDetailAsync` (404 and 200 cases)
- [x] T019 [P] [US2/3] Integration tests for `GET` endpoints

### Implementation
- [x] T020 [US2/3] Add listing/filtering query methods to Repository
- [x] T021 [US2/3] Implement `AdminNotificationService.GetPaginatedAsync` and `GetByIdAsync`
- [x] T022 [US2/3] Implement mapping configurations in `AdminNotificationMapper` for List/Detail Responses
- [x] T023 [US2/3] Implement `AdminNotificationsController.Get` and `GetById` endpoints

## Phase 4: User Story 4 - Delivery Trigger (P2)
### Tests (Write First!)
- [x] T024 [US4] Unit tests verifying Redis fan-out publish happens upon successful creation

### Implementation
- [x] T025 [US4] Integrate Redis publishing logic inside `AdminNotificationService.CreateAsync` processing chunks via fan-out pattern

## Phase 5: Polish & Security
- [x] T026 Ensure `[Authorize(Roles = "Admin")]` and correct JWT caller identity extraction
- [x] T027 Review and ensure no sensitive data is logged in payload/responses
- [x] T028 Final verify with `dotnet test`
