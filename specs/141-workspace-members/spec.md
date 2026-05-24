# Feature Specification: Workspace Member Management (WT-141)

**Feature Branch**: `feat/auth`  
**Created**: 2026-05-24  
**Status**: Draft  
**Input**: Linear ticket WT-141 - [Workspace] Let owners and admins manage workspace members

---

## 1. Problem Statement

As WarpTalk transitions to support collaborative teams, workspace owners and admins need the ability to manage team members securely. Access must be accurate, and the tenant environment must be safe.
To support this model, owners and admins must be able to:
1. **List Workspace Members**: View all active members in a workspace, complete with pagination, search by name/email, and role and status indicators.
2. **Remove Workspace Members / Support Leaving**: Terminate a user's membership (soft delete) or let members leave the workspace, while enforcing owner-role edge cases so that workspaces are never left without an active owner.
3. **Change Member Roles**: Promote or demote workspace members (between Admin and Member), ensuring proper role boundaries (Admins cannot change Owner roles).

Without WT-141:
- **Security Risks**: Former employees or external parties cannot be removed from workspaces, causing data leaks.
- **Role Inflexibility**: Members cannot be promoted to Admins to assist with management, or demoted when their responsibilities change.
- **Orphaned Workspaces**: If owners can remove themselves or demote themselves without transferring ownership, workspaces will become ownerless, leading to unbillable/unmanageable resource states.

---

## 2. Technical Decisions & Architectural Boundaries

### 2.1. Soft-Delete Membership Model
To maintain historical records for billing, audit logs, and meeting transcripts, removing a workspace member is implemented as a **soft-delete**:
- The `WorkspaceMember` entity has `RemovedAt` (DateTime) and `RemovedBy` (Guid, user who removed them) columns.
- Active members must satisfy `RemovedAt == null`.
- The member listing endpoint only returns active members.
- If a user is removed, we set `RemovedAt = DateTime.UtcNow` and `RemovedBy = executingUserId`, and `Status = "Removed"`.

### 2.2. Workspace Member Roles & Rules
WarpTalk supports three roles inside a Business Workspace: `Owner`, `Admin`, and `Member`.

#### Owner Rules:
1. **Highest Authority**: The Owner has full management rights, including the ability to transfer ownership, manage subscriptions, invite members (as Admin/Member), change roles of anyone, and remove anyone.
2. **Owner Protection**:
   - The workspace MUST always have at least **one active owner**.
   - An Owner CANNOT remove themselves (or leave the workspace) if they are the **last remaining active owner**.
   - An Owner CANNOT demote their own role (e.g. to Admin or Member) if they are the **last remaining active owner**.
   - Admins and Members cannot remove or demote the Owner.

#### Admin Rules:
1. **Day-to-day Manager**: Admins can invite new members (as Admin or Member), list members, and change roles.
2. **Role Boundaries**:
   - Admins CANNOT manage the `Owner` role (cannot promote someone to Owner, demote the Owner, or remove the Owner).
   - Admins can change roles of members only between `Admin` and `Member`.
   - Admins can remove `Member`s and other `Admin`s (in line with the approved permission matrix), but NOT the `Owner`.
   - Admins can remove themselves (leave the workspace).

#### Member Rules:
1. **Collaborator**: Members can use workspace resources and view the member list, but cannot modify anything.
2. **Leave Workspace**: Members can remove themselves (leave the workspace).

---

## 3. User Scenarios & Testing (Prioritized Journeys)

### User Story 1 - List Workspace Members (Priority: P1)
*As a workspace member, I want to see the list of active members in my workspace with pagination and search so that I can see who is in my team.*

**Why this priority**: Core visibility feature. All team members need to see their colleagues.

**Independent Test**: Seed a workspace with 12 members. Send a request to `GET /api/v1/workspaces/{workspaceId}/members?page=1&pageSize=10`. Assert that the response contains pagination envelope and 10 active member items.

**Acceptance Scenarios**:
1. **Given** an authenticated member of a workspace,  
   **When** they request the member list,  
   **Then** they receive `200 OK` with a paginated list of active members, including their FullName, Email, Role, JoinedAt, and Status.
2. **Given** an authenticated member of a workspace,  
   **When** they search for "Alice",  
   **Then** they only receive members whose name or email contains "Alice".
3. **Given** a user is NOT a member of the workspace,  
   **When** they request the member list,  
   **Then** they receive `403 Forbidden` or `404 Not Found`.

---

### User Story 2 - Remove Workspace Member / Leave Workspace (Priority: P1)
*As a workspace owner or admin, I want to remove a member from the workspace to revoke their access. As a standard user, I want to be able to leave the workspace.*

**Why this priority**: Fundamental access control and security mechanism.

**Independent Test**: Call the removal endpoint for a member. Verify that `RemovedAt` is set in the database, and the user is no longer returned in the active member list.

**Acceptance Scenarios**:
1. **Given** a workspace Owner or Admin,  
   **When** they send `DELETE /api/v1/workspaces/{workspaceId}/members/{userId}` to remove an active Member,  
   **Then** the member's `RemovedAt` and `RemovedBy` fields are set, and their access is revoked.
2. **Given** an active Member or Admin,  
   **When** they send the DELETE request for their own User ID (self-removal/leave),  
   **Then** the request succeeds and they leave the workspace.
3. **Given** the last remaining Owner of the workspace,  
   **When** they try to remove themselves or leave,  
   **Then** the request is **REJECTED** with `400 Bad Request` explaining they must transfer ownership first.
4. **Given** a workspace Admin,  
   **When** they try to remove the workspace Owner,  
   **Then** the request is **REJECTED** with `403 Forbidden`.

---

### User Story 3 - Change Member Role (Priority: P2)
*As a workspace owner or admin, I want to promote or demote a team member so that their permissions align with their responsibilities.*

**Why this priority**: Required for administrative flexibility.

**Independent Test**: Promote a Member to Admin. Assert that the member's role is updated in the database.

**Acceptance Scenarios**:
1. **Given** a workspace Owner,  
   **When** they change a Member's role to Admin,  
   **Then** the request succeeds and the member is promoted.
2. **Given** a workspace Admin,  
   **When** they try to demote the Owner, or promote someone to Owner,  
   **Then** the request is **REJECTED** with `403 Forbidden`.
3. **Given** the last remaining Owner,  
   **When** they try to demote themselves to Admin or Member,  
   **Then** the request is **REJECTED** with `400 Bad Request`.

---

## 4. Requirements

### Functional Requirements

- **FR-141-001**: System MUST expose `GET /api/v1/workspaces/{workspaceId}/members` supporting pagination (`page`, `pageSize`) and search (`search`).
- **FR-141-002**: Standard workspace members MUST be allowed to view the workspace member list.
- **FR-141-003**: System MUST expose `DELETE /api/v1/workspaces/{workspaceId}/members/{userId}` to remove a member (soft-delete) or let a member leave.
- **FR-141-004**: System MUST check role hierarchy: only Owners and Admins can remove members. Admins CANNOT remove the Owner.
- **FR-141-005**: System MUST block the last remaining active Owner from removing themselves or leaving the workspace.
- **FR-141-006**: System MUST expose `PUT /api/v1/workspaces/{workspaceId}/members/{userId}/role` to change roles.
- **FR-141-007**: Only Owners and Admins can change roles. Admins can only assign `Admin` or `Member` roles and CANNOT change the Owner's role.
- **FR-141-008**: System MUST block the last remaining active Owner from demoting themselves.
- **FR-141-009**: System MUST prevent managing members in a **Personal Workspace** (throws `403 Forbidden` since Personal Workspaces have exactly one member and do not support team features).

---

## 5. Success Criteria

### Measurable Outcomes
- **SC-141-001**: Paginated listing of workspace members must execute in less than 50ms at the database layer.
- **SC-141-002**: 100% of removed members have their `RemovedAt` and `RemovedBy` fields populated accurately; zero hard deletions of history.
- **SC-141-003**: 100% of workspaces remain with at least one active owner at all times.

---

## 6. Assumptions
- A user can only access workspace member management if they are authenticated and authorized under the specific workspace scope.
- Personal Workspaces are restricted from all member management flows.
