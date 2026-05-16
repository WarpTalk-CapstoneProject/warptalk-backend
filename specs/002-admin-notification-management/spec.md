# Feature Specification: Admin Notification Management

**Feature Branch**: `feat/admin-notification-management`  
**Created**: 2026-05-08  
**Status**: Completed  
**Input**: Linear ticket WT-58

## Scope Boundary
**Phase Focus**: This phase focuses strictly on **Option B: create + actual downstream delivery trigger**. The system manages the creation, retrieval, and status lifecycle of notification records, and explicitly triggers downstream delivery via message bus. The actual frontend UI for admins is out of scope.

---

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Create Notification Record (Priority: P1)
As an admin, I want to create a new notification record targeting specific audiences so that the system can prepare it for downstream delivery.
*Note: This story focuses on the API creating a valid record and initiating the trigger, delegating actual push/email mechanisms to downstream workers.*
**Independent Test**: Can be tested by calling the Create Notification API and verifying DB persistence, validation rejections, and event emission.
**Acceptance Scenarios**:
1. **Given** a valid admin token and payload, **When** requested, **Then** it creates a record (Status: Pending) and returns 201 with the ID.
2. **Given** an invalid payload (exceeds max length, malformed), **Then** it returns 400 Bad Request.

### User Story 2 - List Notifications (Priority: P2)
As an admin, I want to view a paginated list of created notifications so that I can track past communications.
**Acceptance Scenarios**:
1. **Given** an authorized admin, **When** requesting the list with date range or title keyword filters, **Then** returns a paginated list with summary metadata (creator, created/updated time, target summary).

### User Story 3 - Get Notification Detail (Priority: P2)
As an admin, I want to retrieve the full details of a specific notification by its ID so that I can audit its payload, exact targeting, and current status.
**Acceptance Scenarios**:
1. **Given** a valid notification ID, **When** requested, **Then** returns the full detail payload.
2. **Given** a non-existent ID, **Then** returns 404 Not Found.

### User Story 4 - Notification Delivery Trigger (Priority: P2)
As the backend system, I want to explicitly trigger downstream delivery for pending notifications and update their lifecycle status.
**Acceptance Scenarios**:
1. **Given** a successfully created notification, **When** persisted, **Then** publish an event to Redis Streams/Message Bus.

---

## Status & Lifecycle Model
Notifications follow a strict state machine. *Note: These status values MUST be defined centrally in a `NotificationConstants` file within the module (e.g., `NotificationConstants.AdminNotificationStatus`) rather than using C# Enums or hardcoded strings.*

- `Draft`: Saved but not ready to send. (Optional, if scheduling is supported later).
- `Pending`: Validated and queued for delivery trigger.
- `Sent`: Successfully picked up/processed by delivery downstream.
- `Failed`: Rejected or failed during downstream delivery.

**Transitions**: 
- Valid: `Pending` -> `Sent`, `Pending` -> `Failed`. 
- Invalid transitions (e.g., `Sent` -> `Pending`) MUST be rejected explicitly.
- *Note on Scheduling*: Currently unsupported. Any request attempting to set a future scheduled date MUST be explicitly rejected or handled as immediate `Pending`.

---

## Requirements *(mandatory)*

### Functional & Common Validation Requirements
- **FR-001**: System MUST reject unsupported notification types and malformed payloads.
- **FR-002**: System MUST reject extra/unknown fields at the top-level payload (strict mode).
- **FR-003**: System MUST enforce max length validation for `Title` and `Content`.
- **FR-004**: System MUST NOT accept raw HTML. Content must be heavily sanitized and normalized.
- **FR-005**: System MUST enforce a URL allowlist for fields like `cta_link` and `image_url` to prevent malicious redirects or insecure assets.

### Notification Types & Payload Schemas
- **FR-020 (Supported Types)**: System MUST support the following specific Notification Types: `PROMOTION`, `SYSTEM`, `ANNOUNCEMENT`, `MAINTENANCE`. These types, along with statuses and target audience modes, MUST be configured centrally in a `NotificationConstants` file. The Database MUST NOT enforce these via `CHECK` constraints; validation is strictly App-layer.
- **FR-021 (Type-Specific Schema Validation)**: Payload MUST be validated against the schema mapped to the declared type. Unknown fields, missing required fields, and fields not allowed for the declared type MUST be rejected.
  - **`PROMOTION`**: may allow `cta_link`, `image_url`, `discount_code`.
  - **`SYSTEM`**: System MUST automatically assign a default high priority to this type. may allow `severity`, `action_required`; promotional fields MUST be rejected.
  - **`ANNOUNCEMENT`**: may allow `image_url`. `cta_link` is optional but MUST be strictly an internal deep-link if provided; promotional discount fields MUST be rejected.
  - **`MAINTENANCE`**: MUST include `downtime_start` and `downtime_end`; `downtime_end` MUST be later than `downtime_start`.

### Target Audience & Eligibility Rules
- **FR-007 (All Users Mode)**: System MUST store "All Users" as a broadcast intent flag. Resolution of actual recipient IDs is deferred to the downstream worker.
- **FR-008 (Segment-based Mode)**: System MUST validate that the referenced Segment ID exists and is active. Actual recipient resolution is deferred to the downstream worker.
- **FR-009 (Specific Users - Duplicate Detection & Validation)**: System MUST detect duplicate target IDs (e.g., by comparing payload array length against a HashSet count) and return a clear validation error if duplicates are found. It MUST also perform a batch query to validate recipient eligibility (must exist and be active).
- **FR-010 (Specific Users - Strict Rejection Policy)**: If ANY target ID in the specific users list is invalid, not found, or ineligible, the system MUST reject the entire request (400 Bad Request). Partial acceptance is NOT allowed.

### Delivery & Performance Constraints
- **FR-011**: System MUST enforce a maximum limit on the number of Specific User IDs allowed in a single request payload (e.g., max 10,000 IDs) to guarantee bounded resource behavior and prevent API timeouts.
- **FR-012**: System MUST utilize a **Batching/Chunking** strategy when validating a large list of IDs against the DB, and when processing downstream delivery (e.g., chunking into groups of 1,000 IDs).

### US4: Delivery Trigger (Fan-out)
**As** the system,
**I want** to trigger a fan-out process
**so that** notifications are accurately distributed to the Target Audience.

#### Design Details:
- System MUST use a Fan-out pattern via the message broker (Redis Streams) to distribute these chunks to downstream workers for parallel delivery.
- **Architecture Flow (Worker to Gateway)**:
  1. **Producer**: `AdminNotificationService` chunks users and calls `StreamAddAsync` to push `DeliveryEventPayload` to Redis Stream `admin-notifications-delivery`.
  2. **Consumer (Worker)**: A Background Service reads chunks via `StreamReadAsync` using Consumer Groups.
  3. **Database**: Worker performs bulk-insert into the PostgreSQL `NotificationMessage` table.
  4. **Pub/Sub**: Worker publishes individual `RealtimeNotificationMessage` events to Redis Pub/Sub channel `warptalk:notifications:new`.
  5. **Gateway**: The Gateway's `NotificationRedisSubscriberService` listens to the Pub/Sub channel and broadcasts to users via SignalR (`NotificationHub`).
- Triggers MUST NOT block the HTTP response. The Admin API must return 200 OK immediately after publishing to the message broker.

### List & Query Requirements
- **FR-014**: The List API MUST support `created_date` range filtering and `title` keyword search.
- **FR-015**: Responses MUST include `creator_id`, `created_time`, `updated_time`, and a `target_audience_summary`.

### Security & Error Handling
- **FR-016**: System MUST derive caller identity (Admin ID) strictly from the server-side auth context (JWT), never from the request body. (Caller Authz)
- **FR-017**: Sensitive data MUST NOT be allowed in the notification payload.
- **FR-018**: System MUST return consistent error formats (ProblemDetails). Permission failures (401/403) MUST be clearly distinguished from Recipient Eligibility / Validation failures (400).

### Operational & Logging
- **FR-019**: Requests MUST include traceable context and correlation IDs to support troubleshooting.
- **FR-020**: System MUST NOT log sensitive payload data.

### Non-Functional Requirements (Architecture & Code Quality)
- **NFR-ARCH-001 (Architecture Preservation)**: The existing architecture of classes, services, handlers, and function boundaries MUST be preserved throughout this feature implementation.
- **NFR-ARCH-002 (No Unapproved Refactor)**: This scope MUST NOT introduce unrelated architectural refactoring, redesign of module structure, or changes to established class responsibilities unless explicitly approved.
- **NFR-ARCH-003 (Separated Mapper Configuration)**: All mappers and mapping profiles MUST be implemented and configured in dedicated files. They MUST NOT be inlined into controllers, handlers, services, or repository classes.
- **NFR-ARCH-004 (Separated Helper Configuration)**: All helper utilities MUST be implemented in dedicated files and MUST NOT be embedded directly inside controllers, handlers, services, or repositories except for trivial private logic local to one class.
- **NFR-ARCH-005 (UnitOfWork Access Pattern)**: Any service requiring persistence access MUST call repositories only through UnitOfWork.
- **NFR-ARCH-006 (No Direct Repository Access from Services)**: Services MUST NOT access repositories directly outside the UnitOfWork abstraction.
- **NFR-ARCH-007 (Consistency of Data Access)**: Data access patterns MUST remain consistent across the module so transaction boundaries, commit behavior, and repository coordination remain centralized in UnitOfWork.
- **NFR-ARCH-008 (Mapper Responsibility Boundary)**: Mappers MUST only handle object transformation. They MUST NOT contain business validation, orchestration, or persistence logic.
- **NFR-ARCH-009 (Helper Responsibility Boundary)**: Helpers MUST only contain reusable support logic. They MUST NOT contain core business workflow logic that belongs in services or handlers.
- **NFR-ARCH-010 (Scope Containment)**: Any change impacting shared architecture, common abstractions, or cross-module design MUST be treated as a separate follow-up item and MUST NOT be bundled into this feature by default.
- **NFR-ARCH-011 (Implementation Compliance)**: Contributors implementing this feature MUST follow the existing architectural conventions and MUST NOT replace them with alternative implementation patterns within this scope.
- **NFR-ARCH-012 (Database First Entity Generation)**: Entities MUST be created using the Database First approach. Contributors MUST write a dedicated SQL migration script for the newly created table ONLY, and place it in the `warptalk-infrastructure/scripts/migrations` directory. The migration execution MUST strictly run this new script to apply the schema without re-running or modifying full database initializations (e.g., `.agents/resources/init-db.sql`). Finally, use the `scaffold` command to generate the C# entity classes.
- **NFR-ARCH-013 (Strict App-Layer State Control)**: The database MUST NOT define `DEFAULT` statuses (e.g. `DEFAULT 'Pending'`) or use Triggers for audit columns (e.g., `updated_at`). All status assignments and timestamp updates MUST be explicitly handled in the Application layer (e.g., inside Mappers).
- **NFR-ARCH-014 (Database Schema Optimization)**: Database schema MUST use optimal indexing strategies, specifically a Composite Index for list queries (`type`, `status`, `created_at DESC`) and a `pg_trgm` GIN Index for fast `ILIKE` keyword searches on titles. The generic metadata field MUST be named `payload`.
---

## Key Entities
1. **AdminNotification**: The core aggregate root storing title, content, type, status, target audience, and audit metadata (including `updated_by`). The JSONB data is stored in `payload`.
2. **TargetAudienceModel**: Value object/Entity defining the intent (`BROADCAST`, `SEGMENT`, `SPECIFIC_USERS`) and associated resolution IDs.
3. **StatusConstant**: Tracks the lifecycle (`Pending`, `Sent`, `Failed`) mapping to the `NotificationConstants` strings. Status transitions are strictly managed by the App layer.
4. **AuditMetadata**: Stores `CreatedBy`, `UpdatedBy`, `CreatedAt`, `UpdatedAt`. `UpdatedAt` is updated manually by the Mapper.
5. **DeliveryEventPayload**: The specific contract published to the message bus for downstream workers.

## Success Criteria *(mandatory)*
- **SC-001**: API covers Create, List, and Detail retrieval with robust validation.
- **SC-002**: Target audience intent is stored efficiently without ID explosion for broadcasts.
- **SC-003**: Logs and errors follow operational and security best practices.
