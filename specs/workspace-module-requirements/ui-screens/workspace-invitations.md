# Screen: Invitations

## Goal

Let Owner/Admin create, list, revoke, resend and track Enterprise Workspace invitations; let invited users preview and accept safely.

## Screen flow

```mermaid
flowchart TD
    A["Open Invitations"] --> B{"Owner/Admin?"}
    B -->|No| C["Forbidden management screen"]
    B -->|Yes| D["Fetch pending, accepted, revoked and replaced invites"]
    D --> E["Create invite form or invite table"]
    E --> F{"Action selected"}
    F -->|Create| G["Validate role, email, domain and external policy"]
    F -->|Resend| H["Replace pending token"]
    F -->|Revoke| I["Confirm revoke"]
    G --> J["Persist invitation and refresh table"]
    H --> J
    I --> J
    K["Invite link opened"] --> L["Preview safe metadata"]
    L --> M["Authenticated user accepts"]
    M --> N["Validate email exact match and expiry"]
    N --> O["Create or reactivate membership"]
```

## RBAC screen variants

| Role / context | Screen behavior |
|---|---|
| Owner | Can create internal/external invitations, resend, revoke and inspect invitation status history. |
| Admin | Can create/revoke/resend within workspace policy; cannot invite Owner role and external invites remain Member-only. |
| Member | No invitation management screen; may accept an invitation only through token preview flow when email matches. |
| External Member | No invitation management screen; token preview is the only invitation-related screen. |
| Invited user | Sees workspace, inviter, target email, role and expiry; token hash is never displayed. |

## Workspace schema touched

| Entity | Fields shown or affected |
|---|---|
| `workspace.workspace_invitations` | `email`, `role_id`, `membership_type`, `status`, `expires_at`, `accepted_at`, `revoked_at`, `replaced_by_invitation_id` |
| `workspace.workspace_verified_domains` | domain validation for internal invitations |
| `workspace.workspace_members` | membership created/reactivated on accept |

## Actions and states

| Action | UI behavior |
|---|---|
| Pick access type | Invite form shows an **Access type** dropdown (`Internal` / `External`) next to Role. It is pre-selected from `GET /workspaces/{id}/invitations/policy?email=…` but the inviter may change it — the form never assigns the access class on its own. |
| Create internal invite | Validate verified domain, role Admin/Member only. |
| Create external invite | Require external collaboration enabled; role forced to Member — the Role dropdown drops `Admin` and locks to `Member` as soon as `External` is picked. |
| Preview invite | Show safe workspace/inviter/role metadata; never show token hash. |
| Accept invite | Require authenticated email exact match; show email mismatch error clearly. |
| Revoke invite | Confirmation dialog; status becomes Revoked. |
| Resend invite | Old pending token becomes Replaced; newest token is active. |

## Requirement baseline behavior

- Add domain policy hint in invite form: verified, unverified, public domain rejected, duplicate enterprise domain.
- A disabled Access type option must state its reason inline, taken from the policy endpoint (`internalDisabledReason` / `externalDisabledReason`) rather than composed client-side: `External` is disabled with a reason when `AllowExternalCollaboration = false`; `Internal` is disabled with a reason when the address is on an unverified domain, and with a **different** reason when it is on a public mailbox domain that can never be verified.
- The form must not re-implement domain matching (verified list, `AllowSubdomains`, public domains). It asks the server about the address being typed, so the client and the server can never hold two versions of the rules.
- Sending an invitation still passes `membershipType` explicitly; the server re-validates and is the decision-maker. The old copy "Internal or External access is assigned automatically from the workspace's verified domains" no longer describes the behaviour and must be removed.
- Add admin-review copy for external collaborator approval when policy requires manual approval.
- Internal invite accept must surface the Internal Home Workspace conflict when the invited account is already `Internal` in another domain-verified Enterprise Workspace; the same account may still accept an external invitation when policy allows it.
