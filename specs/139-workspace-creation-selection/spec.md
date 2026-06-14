# Feature Specification: Workspace Creation and Selection (WT-139)

**Feature Branch**: `feat/workspace-creation-selection`  
**Created**: 2026-05-22  
**Status**: Approved  
**Input**: Linear ticket WT-139 - [Workspace] Allow users to create and select a workspace, and add pagination to list workspaces endpoint.

---

## 1. Problem Statement

A WarpTalk user needs a structured way to partition their collaborative actions, meetings, transcripts, and billing records. This partitioning is achieved via **Workspaces**. A workspace acts as an isolated organizational unit (tenant) within WarpTalk.

To support this model, users must be able to:
1. **Create Workspaces**: Establish a new workspace and automatically become its **Owner** (bootstrapping membership).
2. **List Workspaces**: Retrieve all workspaces they belong to, with support for **Pagination** and search filters to accommodate users who are part of dozens of workspaces.
3. **Get Workspace Details**: Retrieve information about a specific workspace.
4. **Select a Workspace Context**: Define their active working environment so that downstream actions (e.g., room creation, transcript viewing, billing transactions) are correctly scoped.

Without a robust Workspace system:
- **Lack of Multi-Tenancy**: Data cannot be partitioned between organizations or distinct projects, violating enterprise privacy and compliance requirements.
- **Billing Ambiguity**: Meeting minutes and translation credits cannot be accurately billed to specific organizations.
- **Scalability Issues**: A simple un-paginated list of workspaces will experience severe performance degradation (high response payload and latency) as users join more workspaces over time.

---

## 2. Technical Decisions & Architectural Boundaries

### 2.1. Domain Models & DB Schema
To maintain Clean Architecture principles, the Workspace models are defined within the `Auth` or `Workspace` service domain. Since authentication and workspace membership are tightly coupled (users belong to workspaces), the models will be persisted within the `auth` database schema.

#### Workspace Entity
- `Id`: `Guid` (UUID v7, Primary Key)
- `Name`: `string` (max 100, not null)
- `Slug`: `string` (max 100, unique index, URL-friendly representation of name)
- `Description`: `string` (max 500, nullable)
- `LogoUrl`: `string` (max 2048, nullable)
- `CreatedById`: `Guid` (foreign key to `auth.users`, not null)
- `CreatedAt`: `DateTimeOffset` (not null)
- `UpdatedAt`: `DateTimeOffset` (not null)

#### WorkspaceMember Entity (Composite Key Table)
- `WorkspaceId`: `Guid` (foreign key to `auth.workspaces`, composite PK)
- `UserId`: `Guid` (foreign key to `auth.users`, composite PK)
- `Role`: `string` (Enum: `Owner`, `Admin`, `Member`, not null, default `Member`)
- `JoinedAt`: `DateTimeOffset` (not null)

### 2.2. Membership Bootstrapping (Atomic Transaction)
Creating a workspace is a multi-step operation:
1. Validate workspace creation request payload.
2. Generate a unique `Slug` from the `Name` (e.g., "Google DeepMind" -> "google-deepmind"). If the slug already exists, append a random short suffix (e.g., "google-deepmind-4x").
3. Persist the `Workspace` record.
4. Atomicly insert a `WorkspaceMember` record mapping the creator's user ID to the new workspace with the role `Owner`.
*Implementation Rule*: Steps 3 and 4 **MUST** run within a single database transaction to prevent orphaned workspaces without an owner.

### 2.3. Active Workspace Context & Signed Internal Context
To ensure multi-service security and prevent HTTP header spoofing across microservice hops, the active workspace context uses a **Session-based Selection** and **Signed Internal Context** strategy:

1. **Workspace Selection & Active Session**:
   - The user selects their active workspace via `POST /api/workspaces/{id}/select`.
   - The Gateway / Auth service validates the user's membership and stores this mapping (e.g., `active_workspace:{userId} -> {workspaceId}`) in a short-lived Redis session cache (synced with the session JWT lifespan).
2. **Gateway-Level Context Propagation**:
   - For all subsequent incoming client requests, the API Gateway looks up the active workspace ID from the Redis cache using the authenticated user's ID.
3. **Signed Internal Context**:
   - The API Gateway signs this session context (UserId, ActiveWorkspaceId, Role) into an internal cryptographically signed header (e.g., a lightweight HS256 JWT using a shared secret between gateway and microservices, or an HMAC signed header `X-Internal-Context`).
   - Downstream services (e.g., `TranslationRoomService`, `BillingService`) decode and verify this signature to securely resolve the active workspace context, eliminating direct DB queries or unsecured HTTP header trust.

### 2.4. Pagination Contract for Workspace Listing
To prevent database strain and high latency, the `GET /api/workspaces` endpoint must support server-side pagination:
- **Request Parameters**:
  - `page`: `int` (minimum 1, default 1)
  - `pageSize`: `int` (minimum 1, maximum 100, default 10)
  - `search`: `string` (optional, filters by workspace name with partial case-insensitive match)
- **Response Format**: Paginated envelope containing `items`, `page`, `pageSize`, and `total` records.

---

## 3. API Contract Notes

### 3.1. Create Workspace
Create a new workspace. The current authenticated user is automatically bootstrapped as `Owner`.

- **URL**: `POST /api/workspaces`
- **Headers**:
  - `Authorization: Bearer <token>`
  - `Content-Type: application/json`
- **Request Body**:
```json
{
  "name": "WarpTalk Dev Team",
  "description": "Primary workspace for development and testing",
  "logoUrl": "https://cdn.warptalk.vn/logos/dev-team.png"
}
```
- **Response**: `201 Created`
```json
{
  "id": "018f9d0c-1234-7cde-8fgh-ijk123456789",
  "name": "WarpTalk Dev Team",
  "slug": "warptalk-dev-team",
  "description": "Primary workspace for development and testing",
  "logoUrl": "https://cdn.warptalk.vn/logos/dev-team.png",
  "role": "Owner",
  "createdAt": "2026-05-22T16:00:00Z"
}
```

### 3.2. List Workspaces (Paginated)
Retrieve all workspaces the authenticated user belongs to.

- **URL**: `GET /api/workspaces?page=1&pageSize=10&search=Warp`
- **Headers**: `Authorization: Bearer <token>`
- **Response**: `200 OK`
```json
{
  "items": [
    {
      "id": "018f9d0c-1234-7cde-8fgh-ijk123456789",
      "name": "WarpTalk Dev Team",
      "slug": "warptalk-dev-team",
      "description": "Primary workspace for development and testing",
      "logoUrl": "https://cdn.warptalk.vn/logos/dev-team.png",
      "role": "Owner",
      "createdAt": "2026-05-22T16:00:00Z"
    }
  ],
  "page": 1,
  "pageSize": 10,
  "total": 1
}
```

### 3.3. Get Workspace Details
Retrieve detailed information about a specific workspace. The user must be a member of the workspace.

- **URL**: `GET /api/workspaces/{id}`
- **Headers**: `Authorization: Bearer <token>`
- **Response**: `200 OK`
```json
{
  "id": "018f9d0c-1234-7cde-8fgh-ijk123456789",
  "name": "WarpTalk Dev Team",
  "slug": "warptalk-dev-team",
      "description": "Primary workspace for development and testing",
  "logoUrl": "https://cdn.warptalk.vn/logos/dev-team.png",
  "role": "Owner",
  "createdAt": "2026-05-22T16:00:00Z"
}
```
- **Error Responses**:
  - `404 Not Found`: Workspace does not exist or user is not a member (to prevent scanning).

### 3.4. Select Active Workspace Context
Select a workspace to establish it as the active context for subsequent sessions. This stores the active state in Redis.

- **URL**: `POST /api/workspaces/{id}/select`
- **Headers**: `Authorization: Bearer <token>`
- **Response**: `200 OK`
```json
{
  "selectedWorkspaceId": "018f9d0c-1234-7cde-8fgh-ijk123456789",
  "name": "WarpTalk Dev Team",
  "slug": "warptalk-dev-team"
}
```

---

## 4. User Scenarios & Testing (Prioritized Journeys)

### User Story 1 - Workspace Creation & Membership Bootstrapping (Priority: P1)
*As an authenticated user, I want to create a new workspace so that I can start organizing my projects and translation rooms.*

**Why this priority**: Foundational capability. All subsequent workspace activities require a workspace to exist.

**Independent Test**: Send a `POST /api/workspaces` with valid attributes, assert `201 Created`, and verify that the database contains the workspace and a corresponding `WorkspaceMember` record with role `Owner`.

**Acceptance Scenarios**:
1. **Given** an authenticated user,  
   **When** they send `POST /api/workspaces` with `name = "Alpha Project"`,  
   **Then** the system creates the workspace, generates the slug `alpha-project`, sets the user as `Owner`, and returns the workspace details.
2. **Given** an authenticated user,  
   **When** they attempt to create a workspace with a name that is empty or exceeds 100 characters,  
   **Then** the system **REJECTS** with `400 Bad Request` and validation errors.

---

### User Story 2 - Paginated Workspace Listing (Priority: P1)
*As an authenticated user, I want to list the workspaces I belong to with pagination and search filtering so that I can easily find a workspace even if I belong to many.*

**Why this priority**: Required for UI scalability and usability as defined in the ticket request.

**Independent Test**: Seed a test user with 15 workspaces. Send a request to `GET /api/workspaces?page=2&pageSize=10`. Assert that the response contains `page=2`, `pageSize=10`, `total=15`, and the remaining 5 workspaces in the `items` array.

**Acceptance Scenarios**:
1. **Given** a user is a member of 15 workspaces,  
   **When** they request `GET /api/workspaces?page=1&pageSize=10`,  
   **Then** the system returns 10 items and a metadata envelope with `total = 15`.
2. **Given** a user is a member of workspaces "WarpTalk Core" and "Google DeepMind",  
   **When** they request `GET /api/workspaces?search=DeepMind`,  
   **Then** the system returns only "Google DeepMind" in the results list.

---

### User Story 3 - Workspace Selection Context & Secure Internal Context Propagation (Priority: P2)
*As a user belonging to multiple workspaces, I want to select one as my active workspace so that all my current room creations and billable hours are charged to that specific workspace.*

**Why this priority**: Crucial integration point for the multi-tenant microservices model.

**Independent Test**: Call the `/api/workspaces/{id}/select` endpoint, verify success. Assert that active session mapping is stored in Redis cache, and downstream HTTP requests to other microservices contain the cryptographically signed `X-Internal-Context` header.

**Acceptance Scenarios**:
1. **Given** a user belongs to "Workspace A" and "Workspace B",  
   **When** they send `POST /api/workspaces/{Workspace_A_Id}/select`,  
   **Then** the system returns `200 OK` and sets the active workspace context to "Workspace A" in Redis.
2. **Given** a user is NOT a member of "Workspace C",  
   **When** they attempt `POST /api/workspaces/{Workspace_C_Id}/select`,  
   **Then** the system **REJECTS** with `404 Not Found`.

---

## 5. Requirements

### Functional Requirements
- **FR-139-001**: System MUST expose `POST /api/workspaces` to create a workspace.
- **FR-139-002**: System MUST generate a unique, URL-safe slug for each workspace upon creation.
- **FR-139-003**: System MUST atomicly assign the workspace creator as the `Owner` of the workspace.
- **FR-139-004**: System MUST expose `GET /api/workspaces` supporting query parameters: `page`, `pageSize`, `search`.
- **FR-139-005**: The `GET /api/workspaces` response MUST include pagination metadata (`page`, `pageSize`, `total`) and an `items` list.
- **FR-139-006**: System MUST expose `POST /api/workspaces/{id}/select` to set the active workspace context in Redis.
- **FR-139-007**: System MUST validate that a user is a member of the workspace before returning details or allowing selection.
- **FR-139-008**: The API Gateway MUST append a cryptographically signed `X-Internal-Context` header for all downstream microservice invocations with the active workspace context.

---

## 6. Success Criteria & Metrics

### Measurable Outcomes
- **SC-139-001**: **Server-Side Pagination**: Listing workspaces must never load all records from the database in a single query unless explicitly requested with a valid small page size.
- **SC-139-002**: **Transactional Integrity**: 100% of created workspaces must have an associated owner member; zero orphaned workspaces.
- **SC-139-003**: **Context Tamper Prevention**: 100% of downstream microservices must reject internal requests that do not contain a valid, cryptographically verified internal context header.
- **SC-139-004**: **Low Latency Listing**: Paginated listing queries must execute in less than 50ms at the database layer.

---

## 7. Assumptions
- User authentication is handled prior to workspace APIs; all workspace endpoints require a valid JWT token.
- Downstream microservices will consume the workspace context via HTTP Gateway forwarding or unified authentication claims.
