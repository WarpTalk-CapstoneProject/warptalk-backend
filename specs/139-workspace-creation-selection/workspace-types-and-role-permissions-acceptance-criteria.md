# Acceptance Criteria - Enterprise Workspace Roles and Permissions

## Scope

Current Workspace implementation supports a single workspace model: **Enterprise Workspace**.

There is no `WorkspaceType` branch in code and no non-enterprise workspace flow. Workspace behavior is controlled by:

- role assignment: `Owner`, `Admin`, `Member`
- membership type: `Internal`, `External`
- verified domains: `WorkspaceVerifiedDomain`
- settings: `RequireVerifiedDomainForInternal`, `AllowExternalCollaboration`, `AllowSubdomains`
- active membership: `RemovedAt == null`

---

## Enterprise Workspace Creation

### Acceptance Criteria

- An authenticated user can create an Enterprise Workspace explicitly.
- Creating a workspace must generate a unique slug from `Name`.
- The creator must be assigned role `Owner`.
- Owner membership must be created together with the workspace; no ownerless workspace may be persisted.
- If `VerifiedDomains` are provided, the workspace stores those domains as verified domains.
- If `RequireVerifiedDomainForInternal = true` and no domains are provided, the creator email domain is used as the initial verified domain.
- Public email domains such as Gmail/Yahoo must not be accepted as verified enterprise domains.
- A verified domain must not be registered by more than one active workspace.
- If internal domain verification is required, a user who is already an internal member of another Enterprise Workspace must not create or join a second one as internal.
- WarpTalk is multi-workspace: the same account may belong to many Enterprise Workspaces, but only one of them can be the user's Internal Home Workspace.
- Duplicate active verified domains are enforced by backend checks and the `workspace.workspace_verified_domains` partial unique constraint, not by UI inference.

---

## Role Definitions

| Role | Meaning | Main Responsibilities | Restricted Actions |
|---|---|---|---|
| **Owner** | Highest workspace authority | Manage settings, verified domains, external collaboration, invitations, members, roles, ownership, billing policy, documents, access policy | Cannot transfer ownership to an external member; cannot leave/demote self if that would leave no active owner |
| **Admin** | Operational workspace manager | Manage invitations, list members, manage members within allowed boundaries, update basic settings | Cannot transfer ownership; cannot assign Owner; cannot modify `AllowExternalCollaboration`; cannot manage Owner; cannot change another Admin's role; cannot promote members to Admin |
| **Member** | Standard internal collaborator | Use rooms, documents, transcripts and artifacts according to policy; view member list with limited data | Cannot invite, change roles, remove others, update settings, transfer ownership or manage access policy unless explicitly allowed by document ownership rules |

---

## Membership Type Definitions

| Membership type | Meaning | Core access boundary |
|---|---|---|
| **Internal** | User email domain matches verified enterprise domain, or internal membership was explicitly allowed by policy | Can participate in normal workspace collaboration according to role and resource policy |
| **External** | Partner/vendor/outside user invited into the workspace | Cannot be Owner/Admin; cannot receive role beyond `Member`; cannot list full member directory; only accesses resources explicitly allowed or tied to meetings they participate in |

---

## Permission Matrix

| Action | Owner | Admin | Internal Member | External Member |
|---|---:|---:|---:|---:|
| View own workspace membership | Yes | Yes | Yes | Yes |
| Select active workspace | Yes | Yes | Yes | Yes |
| View workspace settings | Yes | Yes | Yes, if active member | Yes, if active member |
| Update basic workspace settings | Yes | Yes | No | No |
| Modify `AllowExternalCollaboration` | Yes | No | No | No |
| Configure verified domains | Yes | Yes, except owner-only settings | No | No |
| Invite internal members | Yes | Yes | No | No |
| Invite external members | Yes, if allowed | Yes, if allowed | No | No |
| Assign Owner | Transfer ownership only | No | No | No |
| Assign Admin/Member | Yes | Limited; cannot promote others to Admin in current code | No | No |
| Remove Member | Yes | Yes | No | No |
| Remove Admin | Yes | No in current code when changing role; removal follows owner/admin boundary but Owner cannot be removed | No | No |
| Remove Owner | No direct removal | No | No | No |
| Transfer ownership | Yes, to active non-external member | No | No | No |
| Leave workspace | Yes, only if not last owner | Yes | Yes | Yes |
| View full member emails | Yes | Yes | No; email hidden in current list mapping | No |
| View member list | Yes | Yes | Yes, internal only | No |
| Manage documents / ACL | Yes | Yes | Own document or explicit policy | Explicit policy/meeting exception only |
| View/download meeting artifacts | Yes | Yes | Based on artifact policy | Only direct participant and within allowed scope/grace period |

---

## Ownership Rules

### Acceptance Criteria

- The workspace must always have an active Owner.
- Only the current `workspace.OwnerId` can transfer ownership.
- New owner must be an active workspace member.
- New owner must not be an external member.
- When ownership is transferred, the previous owner is demoted to Admin and the new owner is assigned Owner role.
- The last remaining owner cannot leave the workspace.
- The last remaining owner cannot demote themselves to Admin or Member.
- A non-owner cannot remove or change the Owner role.

---

## Invitation Rules

### Acceptance Criteria

- Invitation flow applies to Enterprise Workspace.
- Only Owner/Admin may create invitations.
- Admin must not assign Owner role.
- Invitation role must resolve through Auth role catalog.
- Invitation membership type must be either `Internal` or `External`.
- Internal invitation requires a verified domain when `RequireVerifiedDomainForInternal = true`.
- External invitation requires `AllowExternalCollaboration = true`.
- External members can only receive the `Member` role.
- If the invited email already belongs to an active member, the system must reject duplicate membership.
- If a pending invitation exists for the same email, resend must replace the previous invitation and invalidate the old token.
- Expired, revoked, accepted or replaced invitations must not grant access.
- Accepting a valid invitation creates active membership in the target Enterprise Workspace only.
- Accepting a valid internal invitation enforces the single internal Enterprise Workspace rule when domain verification is required.
- Accepting an external invitation is allowed even if the account is already internal in another workspace.

---

## Data Ownership and Workspace Boundary

### Acceptance Criteria

- All workspace-scoped resources must belong to exactly one workspace.
- Workspace permissions must be evaluated within the current workspace context, not at global account level.
- Leaving or removal from a workspace must not move resources to another workspace.
- Removed members must be soft-deleted by setting `RemovedAt`, `RemovedBy`, and `Status = Removed`.
- Historical meeting, transcript, billing and audit records must remain attributable to the original workspace.
- External members must not gain broad workspace data access by membership alone.

---

## Account and Multi-Workspace Rules

### Acceptance Criteria

- A single account can belong to multiple Enterprise Workspaces only when membership constraints allow it.
- A user can be an internal member of at most one domain-verified Enterprise Workspace; this workspace is the user's Internal Home Workspace.
- A user can be an external member of multiple Enterprise Workspaces.
- A user who is already internal in one domain-verified Enterprise Workspace can still join other Enterprise Workspaces as External when those workspaces allow external collaboration.
- Selecting a workspace must store active workspace context with role and membership type.
- Core features such as Translation Room, Meeting Room, Transcript and Billing require a valid active workspace context.

---

## Out of Scope

- Non-enterprise workspace creation or auto-provisioning.
- Any separate workspace type outside Enterprise Workspace.
- Automatic conversion between workspace types.
- Automatic migration of transcript/artifact/document data between workspaces.
- Custom roles beyond `Owner`, `Admin`, and `Member`.
