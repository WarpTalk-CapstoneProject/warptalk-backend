# Implementation Plan: Workspace Invitation Management and Join Requests

**Branch**: `feat/workspace-join-request-flow`
**Date**: 2026-07-27  
**Spec**: `warptalk-backend/specs/160-workspace-join-request-flow/spec.md`

## Summary

Add an Owner/Admin-only Invitations page under the workspace slug route. The page has two pill tabs:

- **Invitations**: invitations sent by Workspace Owner/Admin.
- **Join Requests**: requests created by users from the Workspace Hub Join Workspace flow.

Join Requests always create the `Member` role. Owner/Admin cannot assign `Admin` during approval; role changes remain in the existing workspace member-role flow.

After approval succeeds, WarpTalk sends an approval email to the Join Requester's email address. The email links to the workspace home and is not an acceptance token.

`WorkspaceInvitation` remains the shared persistence model. `Status = REQUESTED` identifies a Join Request. `REVOKED` remains reserved for Owner/Admin revoking an invitation. `REJECTED` is added for a Join Request rejected by Owner/Admin.

## Decisions

1. `MembershipType` is already present on `WorkspaceInvitation` and requires no new type column.
2. A verified email domain creates an `Internal` Join Request.
3. An email that cannot be verified against workspace domains creates an `External` provisional Join Request. It is not an active External Member until approval.
4. A workspace without verified domains accepts Join Requests for manual review; it does not infer `Internal` automatically.
5. At approval, Owner/Admin selects the final membership type:
   - `Internal`: updates the invitation to `Internal`, then creates a new `WorkspaceMember(Member, Internal)` record.
   - `External`: allowed only when `AllowExternalCollaboration = true`, updates the invitation to `External`, then creates a new `WorkspaceMember(Member, External)` record.
6. Rejecting a Join Request sets `Status = REJECTED`; it does not use `REVOKED`.
7. `rejected_reason` is out of scope.
8. `requested_by`, `reviewed_by`, and `reviewed_at` are persisted for tracking.
9. Existing `invited_by` remains required for backward compatibility. For Join Requests it is populated with the requester, while `requested_by` gives the row its explicit meaning.
10. No new audit entity or separate Join Request table is introduced.
11. Approval email is sent only after the invitation/member transaction commits.
12. If approval email delivery fails, membership remains approved and the existing invitation delivery fields record the failure.

## Data Model and Migration Plan

### Existing columns reused

`workspace.workspace_invitations` already contains:

- `membership_type`
- `status`
- `invited_by`
- `accepted_at`
- `created_at`

### New columns

Add a migration under the Workspace Infrastructure migrations location:

```sql
ALTER TABLE workspace.workspace_invitations
  ADD COLUMN requested_by uuid NULL,
  ADD COLUMN reviewed_by uuid NULL,
  ADD COLUMN reviewed_at timestamptz NULL;
```

Add nullable foreign keys to `auth.users`:

```sql
ALTER TABLE workspace.workspace_invitations
  ADD CONSTRAINT workspace_invitations_requested_by_fkey
    FOREIGN KEY (requested_by) REFERENCES auth.users(id) ON DELETE SET NULL,
  ADD CONSTRAINT workspace_invitations_reviewed_by_fkey
    FOREIGN KEY (reviewed_by) REFERENCES auth.users(id) ON DELETE SET NULL;
```

The columns are nullable because existing outbound invitations do not have a requester/reviewer.

Backfill existing Join Requests:

```sql
UPDATE workspace.workspace_invitations
SET requested_by = invited_by
WHERE status = 'REQUESTED'
  AND requested_by IS NULL;
```

Add a lookup index for the two tabs:

```sql
CREATE INDEX workspace_invitations_workspace_status_created_idx
ON workspace.workspace_invitations (workspace_id, status, created_at DESC);
```

### Workspace member insert constraint

Approval must insert a new `workspace_members` row and must not update or reactivate an old member row. The current model has an unconditional unique index on `(workspace_id, user_id)`. If the product supports a user rejoining after a previous membership was soft-removed, replace that index with a partial unique index covering active memberships only:

```sql
-- Use the deployed index/constraint name discovered during migration review.
DROP INDEX IF EXISTS workspace.workspace_members_workspace_id_user_id_key;

CREATE UNIQUE INDEX workspace_members_active_workspace_user_key
ON workspace.workspace_members (workspace_id, user_id)
WHERE removed_at IS NULL;
```

Update the EF mapping with the equivalent `HasFilter("removed_at IS NULL")`. If the deployed database represents the existing uniqueness as a constraint rather than an index, drop the constraint and create the partial unique index instead. The service must still reject approval when an active member already exists.

`status` is a varchar in the current schema, so adding `REJECTED` does not require a PostgreSQL enum migration. If the deployed database has a status check constraint, update it to include `REJECTED`.

### Token hash compatibility

The current schema describes `token_hash` as `NOT NULL`, while Join Request creation currently sets it to `null`. Do not weaken the constraint. Generate a unique dummy hash for Join Requests; Join Requests cannot be accepted through token endpoints while their status is `REQUESTED`.

### Tracking semantics

| Flow | `invited_by` | `requested_by` | `reviewed_by` | `reviewed_at` |
|---|---|---|---|---|
| Admin invitation | Owner/Admin | `NULL` | `NULL` | `NULL` |
| Join Request pending | Requester | Requester | `NULL` | `NULL` |
| Join Request approved | Requester | Requester | Owner/Admin | Approval time |
| Join Request rejected | Requester | Requester | Owner/Admin | Rejection time |

## Backend Plan

### Phase 0 - Tests first

Add failing tests before implementation:

1. Verified-domain Join Request stores `Internal` and `REQUESTED`.
2. No-domain/unmatched-domain Join Request stores provisional `External` and `REQUESTED`.
3. Join Request always resolves the `Member` role, never `Admin`.
4. `requested_by` is populated and `reviewed_by/reviewed_at` are initially null.
5. Duplicate active Join Requests are idempotently returned or rejected according to the existing duplicate policy.
6. Owner/Admin can approve; regular Members cannot.
7. Approval with `Internal` updates the invitation and creates a new `WorkspaceMember(Member, Internal)` record.
8. Approval with `External` fails when external collaboration is disabled.
9. Approval with `External` updates the invitation and creates a new `WorkspaceMember(Member, External)` record when policy allows it.
10. Approval updates the invitation (`MembershipType`, `Status = ACCEPTED`, `AcceptedAt`, `ReviewedBy`, and `ReviewedAt`) and creates the new member record in the same transaction.
11. Rejection sets `REQUESTED -> REJECTED`, `reviewed_by`, and `reviewed_at`.
12. `REVOKED` invitations cannot be treated as rejected Join Requests.
13. Join Request token hash is non-null and cannot be used to accept the request.
14. List filtering separates outbound invitations from `REQUESTED` Join Requests.

### Phase 1 - Domain and DTOs

1. Add `REJECTED` to `InvitationStatus`.
2. Add `RequestedBy`, `ReviewedBy`, and `ReviewedAt` to `WorkspaceInvitation`.
3. Add the same tracking fields to `WorkspaceInvitationDto` where API audit data is required.
4. Add an approval request DTO:

```csharp
public record ApproveJoinRequestRequest(string MembershipType);
```

Role is not part of this DTO and is always resolved as `Member`.

### Phase 2 - Application service

Update `CreateJoinRequestAsync`:

- classify domain server-side;
- use `External` as provisional when internal status cannot be proven;
- populate `RequestedBy` and compatibility `InvitedBy`;
- generate a dummy non-null token hash;
- persist `REQUESTED`.

Update `ApproveJoinRequestAsync`:

- authorize Owner/Admin;
- require `REQUESTED`;
- accept only `Internal` or `External` as final membership type;
- force role `Member`;
- validate external collaboration policy;
- reject the operation if an active `(workspaceId, userId)` member already exists;
- create exactly one new `WorkspaceMember` record with role `Member`;
- never update or reactivate an existing removed member record;
- update the invitation `MembershipType`, `Status = ACCEPTED`, `AcceptedAt`, `ReviewedBy`, and `ReviewedAt`;
- save the invitation update and new member insert in one database transaction;
- if either operation fails, roll back both operations;
- after commit, send the approval email to `invitation.Email`;
- use a dedicated approval email subject/template with a link to `/{workspaceSlug}/home`;
- never include a token or token hash in the approval email;
- do not roll back membership if email delivery fails.

Update `RejectJoinRequestAsync`:

- authorize Owner/Admin;
- require `REQUESTED`;
- set `REJECTED`, `ReviewedBy`, and `ReviewedAt`;
- do not set `AcceptedAt`.

Do not route Join Request approval through token acceptance. Regular invitation acceptance remains unchanged.

### Phase 3 - Persistence

1. Add the migration described above.
2. Map the three new properties in `WorkspaceDbContext`.
3. Extend `WorkspaceInvitationRepository` with status/kind filtering.
4. Ensure list pagination happens after filtering in the database.
5. Verify the existing unique/token constraints against the deployed database before applying the migration.
6. Reuse `DeliveryStatus`, `ProviderMessageId`, `LastSentAt`, and `SentCount` for the latest Join Request approval email; do not add an approval-email table in this phase.

### Phase 4 - API

Keep the existing endpoints and update their behavior:

- `POST /workspaces/join-requests`
- `POST /workspaces/{workspaceId}/join-requests/{invitationId}/approve`
- `POST /workspaces/{workspaceId}/join-requests/{invitationId}/reject`
- `GET /workspaces/{workspaceId}/invitations`

Add approval body support:

```json
{ "membershipType": "Internal" }
```

Add a query filter such as `kind=outbound|join-request` to the list endpoint. Do not expose an API that allows Join Request approval to assign `Admin`.

The approve response reports membership approval separately from email delivery:

```json
{
  "status": "ACCEPTED",
  "membershipType": "Internal",
  "roleName": "Member",
  "approvalEmailStatus": "Sent"
}
```

If delivery fails, return the committed `ACCEPTED` result with `approvalEmailStatus = Failed` and a non-sensitive warning.

### Phase 5 - Approval email

Extend the existing `WorkspaceInvitationEmailComposer` instead of creating a new email service:

1. Add `SendJoinRequestApprovedEmailAsync`.
2. Reuse the existing Resend client and sender configuration.
3. Add an approval-specific HTML/text template.
4. Include workspace name, approved membership type, `Member` role, and workspace-home link.
5. Do not include invitation tokens, token hashes, or sensitive audit identifiers.
6. Persist the delivery result on the invitation after the approval transaction commits.

The approval transaction and email dispatch are separate boundaries: database approval is authoritative, while email delivery is a retryable notification side effect.

## Frontend Plan

### Workspace Hub Join flow

Update `/workspace/join`:

```text
User enters workspace slug
→ POST /workspaces/join-requests
→ persist Status = REQUESTED
→ show request submitted/pending state
→ user waits for Owner/Admin approval
→ do not select or enter the workspace before approval
```

Frontend handler mapping:

1. `src/app/(app)/workspace/page.tsx`
   - Keep the Join Workspace card as navigation to `/workspace/join`.
   - Do not call the API from the card itself; the card has no workspace slug yet.
2. `src/app/(app)/workspace/join/page.tsx`
   - Keep the current slug/URL parser.
   - Replace the current `router.push(/${slug}/rooms)` submit behavior with `useCreateWorkspaceJoinRequest().mutateAsync({ workspaceSlug: slug })`.
   - Disable the submit button while pending.
   - Show API validation, duplicate-request, inactive-workspace, and policy errors inline/toast.
   - On success, show the pending-review state and explicitly tell the user that Owner/Admin approval is required.
   - Do not call workspace selection, update `activeWorkspaceId`, or redirect to `/${slug}/rooms` before approval.
3. `src/services/workspace.service.ts`
   - Add `createJoinRequest(workspaceSlug)` calling `POST /workspaces/join-requests`.
4. `src/hooks/use-workspace.ts`
   - Add `useCreateWorkspaceJoinRequest` and invalidate the user workspace/pending-request queries after success.
5. `src/lib/api/endpoints.ts`
   - Add the `joinRequests` endpoint constant.
6. `src/types/workspace.ts`
   - Add request/response tracking fields: `requestedBy`, `reviewedBy`, and `reviewedAt`.

### Invitations page

Create:

```text
warptalk-web/src/app/(app)/[workspaceSlug]/invitations/page.tsx
```

The page is Owner/Admin-only and contains pill tabs:

- Invitations: show outbound invitation records and existing invite/revoke actions.
- Join Requests: show `REQUESTED` records with requester email, created time, provisional membership type, and actions.

For an unverified/no-domain request, show `Needs review` instead of presenting provisional `External` as final.

Approve opens a compact choice:

- Approve as Internal
- Approve as External

Both options create only the `Member` role.

After approve succeeds, refresh the Join Requests tab and show a success message that the requester was approved and an email was sent. If the API reports `approvalEmailStatus = Failed`, show that the requester is already a member but the email notification needs retry.

Reject calls the reject endpoint and displays the `REJECTED` state.

### Sidebar

Add `Invitations` under the workspace section in `linear-sidebar.tsx`, visible only to Owner/Admin:

```text
/${workspaceSlug}/invitations
```

Keep `/workspace/invitations` as a compatibility redirect or shared component route if existing links depend on it.

### Frontend hooks/services

Add:

- approve/reject service methods;
- approve/reject React Query mutations;
- query filters for outbound and Join Request tabs;
- cache invalidation for invitations and members;
- tracking fields to the frontend workspace invitation type.
- show an email-delivery warning without implying that membership approval failed.

### Related page behavior

- `src/app/(app)/workspace/page.tsx`: keep Create Workspace unchanged; Join Workspace remains the entry point to the slug form.
- `src/app/(app)/workspace/join/page.tsx`: owns parsing, submission, loading, error, and pending-review states.
- `src/app/(app)/[workspaceSlug]/invitations/page.tsx`: Owner/Admin management page with Invitations and Join Requests tabs.
- `src/components/layout/linear-sidebar.tsx`: add the Owner/Admin-only Invitations link.
- `src/app/(app)/layout.tsx`: add Invitations to breadcrumb resolution and keep `/workspace/join` as an onboarding route.
- Existing `/workspace/invitations` links should redirect to the active workspace slug route or render the shared page component.
- Approval notification/email CTA for Workspace A should reuse `useSelectWorkspace`, `setActiveWorkspace`, and the existing workspace navigation behavior.

### Multi-workspace requester screen flow

Join Request state is scoped to the target workspace and must not change the user's current workspace.

```text
User is an active member of Workspace B
→ opens Workspace Hub
→ submits Join Request for Workspace A
→ show confirmation: "Request sent to Workspace A. Waiting for Owner/Admin approval."
→ keep Workspace B available and keep it as the active workspace
→ Workspace A is not added to the workspace switcher yet
```

Workspace Hub should contain two separate sections:

1. **Your workspaces**
   - Shows active memberships such as Workspace B.
   - Clicking B continues to work normally.
2. **Join requests**
   - Shows target Workspace A with `Pending`, `Approved`, or `Rejected` state.
   - Each request is independent; a pending request for A must not block access to B or other active workspaces.

When approval occurs while the user is working in Workspace B:

- do not auto-switch the active workspace;
- refresh the user's workspace/request data on Hub open, window focus, or manual refresh;
- move Workspace A into **Your workspaces** after the new active membership is returned;
- show a notification with an `Open Workspace A` action;
- when the user clicks `Open Workspace A`, use the existing Select Workspace flow to persist the active workspace, update the workspace store, and navigate to `/${workspaceSlug}/home`;
- keep the user in Workspace B until they explicitly open A.

This is a UI/session switch only. It does not grant membership: the backend membership was already created during approval.

When rejected:

- keep Workspace A out of active workspaces;
- show `Rejected` in the Join requests section;
- allow a future request only through the duplicate-request policy; no automatic retry loop.

## Frontend Status Follow-up

Waiting for Owner/Admin approval is mandatory. Add a user-scoped endpoint such as `GET /workspaces/join-requests/mine` for cross-session status rendering:

- return the current user's Join Requests in `REQUESTED`, `ACCEPTED`, and `REJECTED` states;
- keep a local `Pending review` state immediately after successful submission;
- allow the user to return to Workspace Hub;
- refetch the request summary when Workspace Hub opens, window focus returns, or the user manually refreshes;
- do not grant workspace access until the workspace list contains an active membership;
- optionally add realtime notifications later, but do not make them required for the first implementation.

## Verification Plan

Backend:

```powershell
dotnet test workspace/tests/WarpTalk.WorkspaceService.Tests/WarpTalk.WorkspaceService.Tests.csproj
dotnet build workspace/src/WarpTalk.WorkspaceService.API/WarpTalk.WorkspaceService.API.csproj
```

Frontend:

```powershell
npm run lint
npm run build
```

Manual acceptance:

1. User sends a Join Request from Workspace Hub.
2. Owner/Admin sees it in Join Requests, not Invitations.
3. Approve as Internal updates the invitation and creates a new Internal Member record.
4. Approve as External is blocked when policy disallows external collaboration.
5. Approve as External updates the invitation and creates a new External Member record when allowed.
6. No approval path creates an Admin role.
7. Reject produces `REJECTED`; revoking a normal invitation still produces `REVOKED`.
8. Requester/reviewer tracking fields are persisted correctly.
9. Approved requester receives an email linking to workspace home.
10. Approval remains `ACCEPTED` and the new member remains active when approval email delivery fails.

## Out of Scope

- `rejected_reason`.
- A separate Join Request entity/table.
- Assigning `Admin` during Join Request approval.
- Replacing the existing member role-management flow.
- A background audit-event entity or full status history table.
