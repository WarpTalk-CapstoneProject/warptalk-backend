# Implementation Plan: Notification Inbox Persistence

**Branch**: `feat/notification-inbox-persistence` | **Date**: 2026-04-28 | **Spec**: [Link](./spec.md)
**Input**: Feature specification from `/specs/001-notification-inbox-persistence/spec.md`

## Summary

Implement database persistence for the in-app notification inbox with heavy emphasis on ISO-aligned security norms. Add a new `NotificationMessage` entity to the Notification Service, create REST endpoints for retrieving and marking notifications as read (with pagination, rate limiting, and IDOR checks), and update the gRPC and SignalR gateway flows to safely persist messages before broadcasting them across TLS-secured channels.

## Technical Context

**Language/Version**: C# / .NET 9
**Primary Dependencies**: ASP.NET Core, Entity Framework Core, SignalR, gRPC
**Storage**: SQL Server (NotificationDbContext)
**Testing**: xUnit, Postman (for API Contract testing)
**Target Platform**: Backend Microservices

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*
- Security: Endpoints must retrieve `userId` through JWT Claims (`HttpContext.User`). Rate limit middleware must be integrated. XSS encodings applied to JSON response outputs.
- Database: Encryption at Rest policies must be verified for the SQL Server instance if PII exists.
- Networking: TLS for all exposed HTTP endpoints and mTLS considered for internal gRPC.

## Project Structure

### Documentation (this feature)

```text
specs/001-notification-inbox-persistence/
├── plan.md              
├── spec.md              
├── checklists/
│   └── requirements.md 
└── tasks.md             
```

### Source Code (repository root)

```text
src/NotificationService/
├── Controllers/NotificationsController.cs
├── Domain/Entities/NotificationMessage.cs
├── Infrastructure/Data/NotificationDbContext.cs
└── Services/INotificationService.cs

src/Gateway/
└── Hubs/NotificationHub.cs
```

**Structure Decision**: Extending `NotificationService` for business logic (and data-layer encryption) and modifying Gateway WebSockets.
