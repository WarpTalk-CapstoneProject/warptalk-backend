# Feature Specification: Notification Inbox Persistence

**Feature Branch**: `feat/notification-inbox-persistence`  
**Created**: 2026-04-28  
**Status**: Draft  
**Input**: Linear Ticket WT-7 (Updated with Security Requirements)

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Fetch In-App Notifications (Priority: P1)

Users should be able to view their historical in-app notifications so that they do not miss any important alerts when operating the app or after coming back online.

**Why this priority**: Without fetching history, notifications are ephemeral and easily lost, defeating the purpose of an inbox.

**Independent Test**: Can be fully tested by creating a notification in the DB and calling the GET API to retrieve it.

**Acceptance Scenarios**:

1. **Given** a user has multiple notifications, **When** they request their inbox, **Then** the system returns a paginated/sorted list of their newest notifications first.
2. **Given** a user requests notifications, **When** the request is made, **Then** they only see notifications belonging to them, and not to other users.

---

### User Story 2 - Mark Notification as Read (Priority: P1)

Users should be able to mark a specific notification as read so they can keep track of which alerts they've already reviewed.

**Why this priority**: Essential to provide meaningful state for the inbox UI instead of everything always appearing "unread".

**Independent Test**: Can be tested by invoking the PATCH endpoint on a specific notification and verifying the DB state changes to `IsRead = true`.

**Acceptance Scenarios**:

1. **Given** an unread notification, **When** the user marks it as read, **Then** its status updates to `IsRead = true` and `ReadAt` is set.
2. **Given** a notification belongs to user A, **When** user B attempts to mark it as read, **Then** the system denies the action (403/404) (IDOR Prevention).

---

### User Story 3 - Mark All Notifications as Read (Priority: P2)

Users should be able to mark all of their unread notifications as read at once, saving them time from clicking individually.

**Why this priority**: It is a major UX improvement to clear out unread badges quickly.

**Independent Test**: Can be tested by creating multiple unread notifications and invoking the read-all endpoint, ensuring all become "read".

**Acceptance Scenarios**:

1. **Given** a user has 5 unread notifications, **When** they invoke "read all", **Then** all 5 notifications are marked as read in the DB.
2. **Given** user A marks all as read, **When** the operation executes, **Then** user B's unread notifications are NOT affected.

---

### Edge Cases

- What happens when a user creates a notification but the DB insert fails? -> Fails gracefully/retries. Realtime broadcast should wait for persist success.
- How does the system handle fetching notifications if a user has thousands of them? -> Pagination logic applied and rate limited (SR-005).
- What happens when a user attempts to mark an already-read notification as read? -> Idempotent, returns 200 OK without errors.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: System MUST persist a `NotificationMessage` when an in-app notification is generated.
- **FR-002**: System MUST provide a REST API endpoint `GET /api/v1/notifications` to list notifications for the authenticated user, returning a paginated response (`Items`, `TotalCount`, `Page`, `PageSize`).
- **FR-003**: System MUST provide a REST API endpoint `PATCH /api/v1/notifications/{id}/read` to mark a single notification as read.
- **FR-004**: System MUST provide a REST API endpoint `PATCH /api/v1/notifications/read-all` to mark all unread notifications for the authenticated user as read.
- **FR-005**: System MUST broadcast a realtime event via SignalR *after* successfully persisting read states to ensure multi-tab synchronization.
- **FR-006**: System MUST provide REST API endpoints to fetch (`GET /api/v1/notifications/preferences`) and update (`PUT /api/v1/notifications/preferences`) user notification preferences.

### Security Requirements (ISO-Aligned)

- **SR-001 (Auth & IDOR Prevention)**: All inbox and mark-as-read APIs MUST extract `userId` from the server-side access token, and NEVER trust a `userId` passed from the client payload. The system MUST enforce ownership checks when reading or updating read states.
- **SR-002 (Data Privacy & Schema)**: `PayloadJson` MUST strictly enforce a schema to prevent storing unused PII, secrets, or internal credentials. Encryption at rest MUST be configured for any sensitive payloads stored in DB.
- **SR-003 (Transport Security)**: All API and internal service-to-service communication MUST transit over TLS. Internal calls should favor mTLS where infrastructure supports.
- **SR-004 (XSS Prevention)**: Notification contents (`Title`, `Content`, `PayloadJson`) MUST be properly sanitized/encoded before client-side rendering to prevent stored XSS attacks.
- **SR-005 (Availability & Abuse Prevention)**: Inbox APIs MUST implement pagination and rate limiting to mitigate enumeration and abuse.
- **SR-006 (Audit & Retention)**: Define a clear retention policy for historical notifications and set up audit logging for sensitive actions to aid incident investigation.

### Key Entities

- **NotificationMessage**: Represents an individual notification in the inbox.
  - Attributes: `Id`, `UserId`, `Type`, `Title`, `Content`, `ActionUrl` (optional), `PayloadJson`, `IsRead`, `ReadAt`, `CreatedAt`.
- **NotificationPreference**: User preferences for notification channels.
  - Attributes: `Id`, `UserId`, `NotificationType`, `EmailEnabled`, `PushEnabled`, `InAppEnabled`, `UpdatedAt`.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: Users can retrieve their notification history via the REST API with a valid response.
- **SC-002**: Read states are accurately persisted across sessions.
- **SC-003**: A single notification delivery updates the DB before the realtime broadcast is successfully sent to the user.
- **SC-004**: Security verification passes for IDOR (user cannot read/mark others' mail) and XSS (injected payloads are sanitized on render).

## Assumptions

- We are adding to an existing `NotificationDbContext` inside a .NET microservice.
- Authentication context is already available to controllers to securely identify "current user" via `HttpContext.User`.
- Pagination uses an offset-based model by default returning a wrapped response with `Page` and `PageSize` capped at max 100.
