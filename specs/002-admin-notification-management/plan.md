# Implementation Plan: Admin Notification Management

**Branch**: `feature/wt-58-bo-sung-notification-management-cho-admin-portal` | **Date**: 2026-05-09 | **Spec**: [002-admin-notification-management/spec.md]
**Input**: Feature specification from `specs/002-admin-notification-management/spec.md`

## Summary
Implement backend capabilities for admin-initiated notifications with a strict focus on robust validation, precise target audience resolution, and separated architectural boundaries. This involves APIs to Create, List, and Retrieve Details of notifications, applying strict schema validations based on Notification Type, enforcing specific user eligibility, and implementing chunking/fan-out delivery triggers.

## Technical Context
**Language/Version**: C# 12 / .NET 10
**Primary Dependencies**: ASP.NET Core, EF Core, FluentValidation, StackExchange.Redis
**Storage**: PostgreSQL
**Testing**: xUnit, Moq, Testcontainers
**Project Type**: ASP.NET Core Web API (WarpTalk.NotificationService.API)
**Constraints**: Clean Architecture, TDD, Architectural NFRs (Strict Separation), Database First Entity Generation

## Constitution & Architecture Check
*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

- [x] Article I: 4 Layers (Domain, Application, Infrastructure, API). No leakage.
- [x] Article II: gRPC for sync, Redis for async.
- [x] Article IV: TDD - tests before code.
- [x] **NFR-ARCH-001/002**: Existing boundaries preserved; no unapproved refactors.
- [x] **NFR-ARCH-003/004**: Mappers and Helpers must be strictly isolated into dedicated files.
- [x] **NFR-ARCH-005/007**: All persistence via `IUnitOfWork` ONLY. No direct `IRepository` injection into Services.
- [x] **NFR-ARCH-012/013/014**: Database First Entity Generation via SQL script. DB schema MUST NOT contain constraints or triggers for app logic. Proper indexing (Composite, Trigram) applied.

## System Design Decisions

### 1. Database Schema Optimization & Strict App-Layer Control
- **Database First**: Created `005-09-05-2026-add-admin-notifications-table.sql` mapping `payload` to JSONB.
- **No DB Magic**: Removed `DEFAULT` status and `updated_at` triggers from the DB. State transitions and timestamps are fully managed in C# Mappers (`AdminNotificationMapper`).
- **Indexing**: Applied Composite index `(type, status, created_at DESC)` and Trigram GIN index `idx_admin_notif_title_trgm` on `title`.

### 2. Payload Polymorphism & Type Validation
- **Constants**: Extract types (`PROMOTION`, `SYSTEM`, etc.) and statuses to `NotificationConstants.cs`.
- **Validation**: Utilize FluentValidation with a Strategy pattern or `When()` conditions to enforce strict schema rules based on the `Type` field.
- **Top-level Strict Mode**: Configure the model binder or validator to reject unknown top-level fields to comply with `FR-002`.

### 3. Target Audience Handling & Chunking
- **Specific Users**: Implement deduplication (`HashSet`) and a batch query (`IN` clause) to validate user eligibility. Enforce the fail-fast strict rejection policy (return 400 if any ID is invalid). Max limit 10,000.
- **Chunking/Fan-out**: Use LINQ `.Chunk()` to divide large recipient lists. Publish smaller batch events to Redis Streams inside the Create workflow.

## Project Structure
```text
src/
├── WarpTalk.NotificationService.Domain/
│   ├── Constants/NotificationConstants.cs
│   ├── Entities/AdminNotification.cs
│   └── Interfaces/IAdminNotificationRepository.cs
├── WarpTalk.NotificationService.Application/
│   ├── DTOs/AdminNotifications/
│   ├── Mappers/AdminNotificationMapper.cs         <-- NFR-ARCH-003
│   ├── Services/AdminNotificationService.cs
│   ├── Validators/AdminNotificationValidator.cs
│   └── Helpers/NotificationValidationHelper.cs    <-- NFR-ARCH-004
├── WarpTalk.NotificationService.Infrastructure/
│   └── Repositories/AdminNotificationRepository.cs
└── WarpTalk.NotificationService.API/
    └── Controllers/AdminNotificationsController.cs
```
