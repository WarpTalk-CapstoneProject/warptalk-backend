# Acceptance Criteria - Enterprise Workspace Roles and Permissions

## Scope

Current Workspace implementation supports a single workspace model: **Enterprise Workspace**.

There is no `WorkspaceType` branch in code and no non-enterprise workspace flow. Workspace behavior is controlled by:

- role assignment: `Owner`, `Admin`, `Member`
- membership type: `Internal`, `External`
- verified domains: `WorkspaceVerifiedDomain`
- settings: `AllowExternalCollaboration`, `AllowSubdomains`
- derived policy: `RequireVerifiedDomainForInternal` (see below — a value, not a setting)
- active membership: `RemovedAt == null`

### Membership assignment policy

Every workspace is an Enterprise Workspace. What differs between two of them is how far the Owner's `Internal`/`External` choices are constrained, and that follows from one fact:

```
require_verified_domain_for_internal  ==  (workspace holds at least one active verified domain)
```

| Policy | Holds verified domains | Constraint on choosing `Internal` |
|---|---|---|
| **Domain-verified membership** | yes | the invitee's address must be on one of them |
| **Manually-assigned membership** | no | none — the Owner draws the line by hand |

`RequireVerifiedDomainForInternal` is **derived and not settable**. Nobody — including the Owner — turns it on or off; adding the first verified domain turns it on and revoking the last one turns it off. `PATCH /workspaces/{id}/settings` refuses any value that disagrees with the domain list. Storing the policy separately from the domains it describes is what allowed a workspace to require a verified domain while holding none, and to hold domains with the requirement switched off (WT-179).

Manually-assigned membership is **not** a lesser workspace, a "personal" workspace, or a separate type. `Internal` and `External` mean the same thing there as anywhere else.

---

## Enterprise Workspace Creation

### Acceptance Criteria

- An authenticated user can create an Enterprise Workspace explicitly.
- Creating a workspace must generate a unique slug from `Name`.
- The creator must be assigned role `Owner`.
- Owner membership must be created together with the workspace; no ownerless workspace may be persisted.
- The founder chooses the membership policy at creation. `RequireVerifiedDomainForInternal` in the request is an **intent** ("claim my email domain"), not the stored value; the stored value is derived from the domains the workspace ends up holding, so the two can never contradict each other and `{requireVerifiedDomainForInternal: false, verifiedDomains: [...]}` needs no error of its own.
- If `RequireVerifiedDomainForInternal` is omitted, the workspace is domain-verified and claims the creator's email domain. The weaker policy is chosen deliberately, not fallen into by leaving a field out.
- If `VerifiedDomains` are provided, the workspace stores those domains as verified domains.
- Public email domains such as Gmail/Yahoo must not be accepted as verified domains, and an account on one cannot create a **domain-verified** workspace. It can create a manually-assigned one: that rule protects the trusted Internal tier, and a workspace claiming no domain hands out no such tier.
- A verified domain must not be registered by more than one active workspace. Enforced by backend checks and by the partial unique index on `lower(domain) WHERE status = 'verified'` — case-insensitively, so `ACME.com` and `acme.com` are one domain.
- A workspace may hold **several** verified domains (`acme.com` and `acme.vn` for one company). Claiming a domain other than the caller's own account domain is recorded as `self_asserted` and requires the Owner's explicit consent; claiming their own is `owner_email` and requires none.
- A user who is already an internal member of another domain-verified Enterprise Workspace must not create or join a second one as internal. This is unconditional and cannot be switched off from the request body. A manually-assigned workspace does not consume that slot.
- WarpTalk is multi-workspace: the same account may belong to many Enterprise Workspaces, but only one of them can be the user's Internal Home Workspace.

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
| **Internal** | Chosen by the inviting Owner/Admin. Under domain-verified membership the choice is only available for addresses on a verified domain; under manually-assigned membership it is available for any address | Can participate in normal workspace collaboration according to role and resource policy |
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
| Configure verified domains | Yes | **No** | No | No |
| View verified domains | Yes | Yes (read-only) | No | No |
| Modify `RequireVerifiedDomainForInternal` | **Nobody** — derived from the verified domains | Nobody | Nobody | Nobody |
| Invite internal members | Yes | Yes | No | No |
| Invite external members | Yes, if allowed | Yes, if allowed | No | No |
| Assign Owner | Transfer ownership only | No | No | No |
| Assign Admin/Member | Yes | Limited; cannot promote others to Admin in current code | No | No |
| Remove Member | Yes | Yes | No | No |
| Remove Admin | Yes | No in current code when changing role; removal follows owner/admin boundary but Owner cannot be removed | No | No |
| Remove Owner | No direct removal | No | No | No |
| Transfer ownership | Yes, to an active non-external member whose address is on one of the workspace's verified domains (vacuous when it holds none) | No | No | No |
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
- New owner's email must be on one of the workspace's active verified domains. Stated without branching on the policy: a workspace holding no verified domains has an empty set here, so the rule is vacuous and only the External check applies. "Not external" does not cover this on its own — that reads the stored `MembershipType`, and a member keeps the type they were granted even after the workspace starts verifying domains.
- When nobody qualifies, the way out is to revoke the verified domains: the workspace returns to manually-assigned membership and the rule empties. Ownership is never forced through, and deleting the workspace is never the only remedy.
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
- Invitation membership type must be either `Internal` or `External`, it is **chosen by the inviter**, and it is **required** on the request. The system does not derive it from the email domain and does not fall back to inferring it: an omitted membership type is a malformed request, not a request to guess. Inference could not express what an inviter might want — `External` was unreachable whenever the invitee's address happened to be on a verified domain, and unreachable outright under manually-assigned membership, where it answered `Internal` for every address.
- Internal invitation requires a verified domain under domain-verified membership; `AllowSubdomains` applies at both create time and accept time.
- Internal invitation to a public mailbox domain (Gmail, Yahoo, …) is rejected under its own error code under domain-verified membership — such a domain can never be verified, so the unverified-domain remedy does not apply to it.
- Under manually-assigned membership, no **domain** validation runs at all: any address may be invited as `Internal`. Validation that is not about domains still runs in both policies — `AllowExternalCollaboration` and "External may only hold the Member role" are rules about membership type, not about domains.
- Join requests follow the same rule. Under manually-assigned membership the reviewing Admin may approve either `Internal` or `External`; the request's inferred type must not narrow the reviewer's options to `External` only.
- External invitation requires `AllowExternalCollaboration = true`.
- External members can only receive the `Member` role, enforced on the accept path as well as the create path.
- Acceptance re-checks the stored intent against the settings in force at that moment and may only admit it unchanged or reject it; it must never rewrite the membership type into one that passes.
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

- Any separate workspace **type** outside Enterprise Workspace, and any `WorkspaceType` column. Domain-verified and manually-assigned are two membership policies of the one model, not two kinds of workspace — the distinction is the value of one derived flag.
- Auto-provisioning a workspace for a user who has none.
- Reclassifying existing members when a workspace's membership policy changes. Adding the first verified domain or revoking the last one changes what future invitations and join requests are allowed; members keep the `MembershipType` they were granted.
- Real domain verification (DNS TXT, token, email challenge) — WT-157. Until then a claim rests on the claiming account's own email domain, or on the Owner's recorded assertion.
- Platform-level configuration of the rules above by a system admin — WT-360.
- Automatic migration of transcript/artifact/document data between workspaces.
- Custom roles beyond `Owner`, `Admin`, and `Member`.
