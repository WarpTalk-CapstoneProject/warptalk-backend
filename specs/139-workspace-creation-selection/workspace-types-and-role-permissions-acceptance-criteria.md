# Acceptance Criteria — Workspace Types and Role Permissions

## Scope
Define workspace behavior for two workspace types:

- **Personal Workspace**
- **Business Workspace**

Also define role-based permissions for **owner**, **admin**, and **member** inside a business workspace.

---

## Workspace Types

### Personal Workspace
A personal workspace is the default workspace automatically created when a new account registers.

#### Acceptance Criteria
- A new account must automatically receive exactly **one** personal workspace.
- The default personal workspace name must be `<FullName>'s Workspace`.
- The account owner must be the **owner** of that personal workspace.
- A personal workspace must have exactly **one active member**.
- A personal workspace must not allow inviting additional members.
- A personal workspace must not allow ownership transfer.
- A personal workspace must remain personal when the user joins or creates a business workspace.
- A personal workspace must not automatically convert into a business workspace.
- Transcripts and artifacts created in a personal workspace must belong to that personal workspace only.
- Personal workspace data must be visible and manageable only by its owner.

---

### Business Workspace
A business workspace is explicitly created by a user or joined through an invitation.

#### Acceptance Criteria
- A user must be able to create a business workspace explicitly.
- A user must be able to join a business workspace through a valid invitation.
- A user may belong to multiple business workspaces at the same time.
- Joining a business workspace must not modify or replace the user’s personal workspace.
- A business workspace must support multiple active members.
- A business workspace must support the roles **owner**, **admin**, and **member**.
- A business workspace must always have at least **one owner**.
- Transcripts and artifacts created in a business workspace must belong to that business workspace.
- Business workspace data must remain inside the workspace even if a member leaves it.

---

## Role Definitions in Business Workspace

| Role | Meaning | Main Responsibilities | Restricted Actions |
|---|---|---|---|
| **Owner** | Highest authority in the workspace | Manage workspace settings, invitations, members, roles, ownership, billing, and deletion | Cannot leave, be removed, or be demoted if that would leave the workspace without any owner |
| **Admin** | Operational manager for day-to-day workspace administration | Manage invitations, members, and basic workspace settings | Cannot transfer ownership, assign/remove owner role, manage billing, or delete workspace |
| **Member** | Standard collaborative user in the workspace | Use workspace resources and collaborate on shared data | Cannot manage invitations, roles, members, billing, ownership, or delete workspace |

---

## Permission Matrix

| Action | Personal Workspace | Business Owner | Business Admin | Business Member |
|---|---|---:|---:|---:|
| View workspace | Yes | Yes | Yes | Yes |
| Edit workspace basic info | Yes | Yes | Yes | No |
| View transcripts/artifacts in workspace | Yes | Yes | Yes | Yes |
| Create transcripts/artifacts in workspace | Yes | Yes | Yes | Yes |
| Edit/delete own transcripts/artifacts | Yes | Yes | Yes | Yes |
| Edit/delete other members’ transcripts/artifacts | N/A | Yes | Yes or limited by policy | No |
| View member list | N/A | Yes | Yes | Yes or limited by policy |
| Invite members | No | Yes | Yes | No |
| Resend/revoke invitation | No | Yes | Yes | No |
| Change member role to admin/member | No | Yes | Yes | No |
| Assign owner role | No | Yes | No | No |
| Remove member | No | Yes | Yes | No |
| Remove admin | No | Yes | Yes | No |
| Remove owner | No | Yes, only if at least one owner remains | No | No |
| Transfer ownership | No | Yes | No | No |
| Leave workspace | No | Yes, only if another owner remains | Yes | Yes |
| Manage billing/subscription | No | Yes | No | No |
| Delete workspace | No | Yes | No | No |

---

## Membership and Ownership Rules

### Personal Workspace
#### Acceptance Criteria
- The personal workspace owner must be the same user as the account owner.
- The personal workspace must not allow additional active members.
- The personal workspace must not expose invitation flows.
- The personal workspace must not support role changes.
- The personal workspace must not support ownership transfer.

### Business Workspace
#### Acceptance Criteria
- A business workspace must always have at least one owner.
- The last remaining owner must not be allowed to leave the workspace.
- The last remaining owner must not be removed from the workspace.
- The last remaining owner must not be demoted to admin or member.
- Only an owner may assign or transfer the owner role.
- An admin may only change roles between **member** and **admin**.
- A member must not be allowed to change any role assignments.

---

## Invitation Rules

#### Acceptance Criteria
- Invitation flow must exist only for business workspaces.
- Only owner and admin may invite users into a business workspace.
- If an invited email already belongs to an active member of the workspace, the system must not create a duplicate invitation.
- If an invited email already has a pending invitation, the system must define one consistent behavior:
  - resend the existing invitation, or
  - replace it with a new valid invitation and invalidate the old one
- Expired or revoked invitations must not grant workspace access.
- Accepting a valid invitation must create or activate membership in the target business workspace only.

---

## Data Ownership and Workspace Boundary

#### Acceptance Criteria
- All transcripts and artifacts must belong to exactly one workspace.
- Data created inside a personal workspace must remain under that personal workspace.
- Data created inside a business workspace must remain under that business workspace.
- Joining a business workspace must not migrate personal workspace data automatically.
- Leaving a business workspace must not move business workspace data into the user’s personal workspace.
- Workspace permissions must be evaluated within the context of the current workspace, not at global account level.

---

## Account and Multi-Workspace Rules

#### Acceptance Criteria
- A single account must be able to exist in multiple workspaces.
- A single account must always have one personal workspace.
- A single account may additionally belong to zero or more business workspaces.
- Switching into a business workspace must not change the type of the personal workspace.
- Personal and business workspace types must coexist independently under the same account.

---

## Active Workspace Access Control

#### Acceptance Criteria
- **Mandatory Active Workspace**: Any user attempting to access other core features of the application (including **Translation Room**, **Meeting Room**, **Transcript**, and **Billing** consumers) MUST have an associated and active workspace ID selected.
- **Access Blocking**: If a user does not have a workspace ID linked (or if the linked workspace is inactive/disabled), they must be blocked from accessing these functions until a valid workspace is created or selected.

---

## Out of Scope
- Automatic conversion from personal workspace to business workspace
- Automatic migration of transcripts or artifacts between workspace types
- Advanced custom roles beyond owner, admin, and member
