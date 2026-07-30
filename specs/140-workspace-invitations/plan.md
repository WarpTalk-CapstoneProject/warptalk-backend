# Implementation Plan: Email-Bound Workspace Invitation via Resend

**Branch**: `feat/workspace-email-bound-invitation-resend` | **Date**: 2026-07-24 | **Spec**: `specs/140-workspace-invitations/spec.md`  
**Input**: Implement Workspace invitation delivery through Mailbox using Resend provider and align with `.agents/resources/sequence diagram/workspace/`.

## Summary

Migrate Workspace invitations to the canonical `Email-Bound Invitation Resolution` model from `CONTEXT.md`: the invitation is bound to the invited email address, not to a secure token in a link. Resend is used only to deliver an email notification. The invitee opens WarpTalk, authenticates with the same verified email address, and Workspace Service resolves any pending invitation by that authenticated email.

This is intentionally larger than simply adding Resend to the current token-based implementation, because Auth, Workspace, Web, gRPC contracts, specs, and sequence diagrams currently still depend on `RawToken` / `TokenHash` flows.

## Invitation Lifecycle Clarification

For the current phase, keep invitation lifecycle intentionally simple:

- **Retry email**: re-attempt Resend delivery for the same existing `PENDING` invitation record. This does not create a new invitation.
- **Retry email eligibility**: allow retry only when `deliveryStatus = Failed` for the current `PENDING` invitation.
- **Invitation immutability after send**: once an invitation has been sent to mailbox, Owner/Admin cannot update its role, membership type, expiration, or other content in place.
- **Change invitation content**: if Owner/Admin needs different invitation content, they must revoke the current invitation and create a new one manually.

`REPLACED` is intentionally deferred out of the current implementation scope to avoid extra lifecycle complexity in API, UI, and tests.

## Problem Found During Plan Review

The previous plan had an internal contradiction:

- It said `RawToken` should be completely removed from API responses and email URLs.
- It also preserved current token-based accept/register flow in the risk section.

Current code is token-based:

- Workspace API has preview/accept endpoints that require token.
- Auth `RegisterInvitedAsync` verifies and accepts invitation by token through Workspace gRPC.
- Web builds `/invitations/{result.rawToken}` after creating an invite.
- Sequence diagrams `02` and `03` show token parsing and token accept.

The corrected plan below treats token-based invitation as legacy behavior to remove, not as the target design.

## Scope

- Keep invitation creation owned by Workspace Service.
- Add Resend-backed mailbox delivery.
- Remove secure invitation token generation, storage, API exposure, email links, preview, and accept-by-token from the target flow.
- Add pending invitation resolution by authenticated, verified email.
- Update Auth registration/login onboarding to resolve workspace invitations after email verification.
- Update Web invitation/onboarding screens so the visible UX lands on workspace home after login/verification, while any invitation resolver route remains an internal callback step.
- Persist delivery metadata for email notification audit and retry visibility.
- Update sequence diagrams under `.agents/resources/sequence diagram/workspace/`.

## Out of Scope

- Moving invitation ownership to Notification Service.
- Reworking all notification preferences.
- Resend webhook handling for bounce/open/click events.
- Billing approval, document grants, or meeting grants beyond membership creation.

## Architecture Decision

Use two separate concepts:

- **Workspace Invitation**: domain resource that grants a pending workspace membership opportunity to one email address.
- **Invitation Email Delivery**: infrastructure delivery attempt that notifies that email address via Resend.

Do not use email delivery as authorization. The authorization gate is the authenticated WarpTalk account email, and it must be verified.

## Canonical Flow

1. Owner/Admin invites `invitee@example.com` to a workspace.
2. Workspace Service validates workspace role, membership type, verified-domain policy, and external collaboration policy.
3. Workspace Service replaces any active pending invitation for the same workspace/email.
4. Workspace Service creates a new `PENDING` invitation without generating a raw invite token.
5. Workspace Service sends a notification email through `IWorkspaceInvitationMailbox`.
6. Invitee clicks `Accept & Join Workspace`, whose intended destination is `/{workspaceSlug}/home`.
7. If the invitee is not authenticated, Web redirects to login/register with a safe callback that preserves the intended workspace home URL and non-secret workspace slug context.
8. After login/register, Auth returns the user to the Web callback. Auth does not accept Workspace invitations itself.
9. If the user's email is verified, Web performs a short invitation resolver step: fetch pending invitations by authenticated email, find the invitation matching the workspace slug, accept it, select the accepted workspace as active, then redirect to `/{workspaceSlug}/home`.
10. Workspace Service revalidates invitation status, expiry, email match, domain policy, internal-home constraint, and duplicate membership.
11. Workspace Service creates the `WorkspaceMember` and marks the invitation `ACCEPTED` in one transaction.
12. If email is not verified, Web shows the verification state and stores the safe invitation callback. After verification succeeds, Web resumes the resolver and redirects to `/{workspaceSlug}/home`.
13. If the matching invitation has already been `REVOKED` or `EXPIRED`, Web shows a clear terminal invitation status page and does not continue resolver or redirect to home.
14. If no matching invite exists or multiple ambiguous invites exist, Web shows the onboarding state instead of redirecting to home.

Expiration handling:

- Use lazy expiration in the current phase.
- When resolver, pending lookup, or accept flow reads an invitation whose `ExpiresAt < now`, treat it as `EXPIRED` and persist the status transition at that time.
- Do not add a background expiration job in this phase.

## API Response Semantics

For `POST /api/v1/workspaces/{workspaceId}/invitations`, prefer `201 Created` whenever the `WorkspaceInvitation` resource is created successfully, regardless of whether Resend delivery succeeds in the same request.

- `201 Created` + `deliveryStatus = Sent`: invitation created and email delivered successfully.
- `201 Created` + `deliveryStatus = Failed`: invitation created, but email delivery failed; return a warning so Owner/Admin can retry email delivery later.
- 4xx / 5xx: use only when invitation creation itself fails, or the request is invalid/unauthorized.

Reasoning:

- The primary business outcome is creation of the pending invitation resource.
- Email delivery is a side effect and should not redefine the resource creation result.
- This keeps the API aligned with the user's intent: inviting an email into the workspace.

## Data Model Changes

### WorkspaceInvitation

Remove from target model:

- `TokenHash`

Keep or add:

- `Email`
- `Status`: `PENDING`, `ACCEPTED`, `REVOKED`, `EXPIRED`, `REQUESTED`
- `ExpiresAt`
- `AcceptedAt`
- `RoleId`
- `MembershipType`
- `InvitedBy`
- `DeliveryStatus`: `NotSent`, `Sent`, `Failed`
- `ProviderMessageId`: nullable Resend message id
- `LastSentAt`: nullable UTC timestamp
- `SentCount`: integer retry/resend attempt counter

Indexes:

- Unique active pending lookup should be app-enforced and supported by `(workspace_id, lower(email), status)`.
- Add `(lower(email), status, expires_at)` for authenticated-email onboarding lookup.
- Add `(workspace_id, delivery_status, created_at DESC)` only if admin retry/list filtering needs it.

Migration:

- Expand first: add delivery fields and new repository queries.
- Migrate code to stop reading/writing `TokenHash`.
- Contract tests pass without token endpoints.
- Contract later: drop `TokenHash` column when no runtime path depends on it.

Lifecycle semantics:

- `PENDING`: invitation is active and can still be accepted.
- `ACCEPTED`: invitation was consumed and membership was created.
- `REVOKED`: invitation was canceled by Owner/Admin.
- `EXPIRED`: invitation passed its acceptance window.

Expiration semantics:

- `EXPIRED` is materialized lazily on read or accept attempt.
- Admin list/filter views should surface the latest computed status after lazy expiration is applied.
- Opening the Owner/Admin invitation list materializes overdue pending invitations as `Expired` before returning the response.

Delivery semantics:

- `DeliveryStatus = Failed` does not change invitation business status.
- Repeated delivery attempts update the same invitation record.
- `SentCount` counts email delivery attempts for the same invitation.
- Invitation content becomes immutable after the first mailbox send.
- `Retry email` is available only when the latest delivery status is `Failed`.

## Shared Resend Integration

Place reusable provider plumbing in `WarpTalk.Shared`, while keeping Workspace-specific email composition in Workspace:

1. Define `ResendOptions` and `IResendEmailClient` in `WarpTalk.Shared`.
2. Provide `services.AddResendEmailClient(configuration)` in `WarpTalk.Shared`.
3. Implement `ResendEmailClient` using `HttpClientFactory`.
4. Implement `IWorkspaceInvitationMailbox` inside Workspace Infrastructure and delegate provider calls to `IResendEmailClient`.

Reasoning:

- Other modules can reuse the Resend HTTP client.
- Workspace still owns invitation language, body content, action URL, and delivery state.
- Application layer depends on `IWorkspaceInvitationMailbox`, not Resend types.

## Configuration

```json
"Resend": {
  "ApiKey": "",
  "FromEmail": "no-reply@warptalk.vn",
  "FromName": "WarpTalk",
  "AppBaseUrl": "https://app.warptalk.vn"
}
```

Runtime requirements:

- Set `RESEND__APIKEY` from environment/secrets.
- Do not commit a real API key.
- If Resend config is missing or provider call fails, keep invitation `PENDING` and record `DeliveryStatus = Failed`.

Sender resolution:

- Default sender is `Resend:FromEmail`.
- Workspace verified-domain sender can be added later only after domain ownership and Resend domain verification are both represented explicitly. Do not infer sender address from `WorkspaceVerifiedDomain` alone.

## Backend Work Plan

### Phase -1 - Align Specs and Diagrams

1. Update `specs/140-workspace-invitations/spec.md`:
   - remove token-hash/token-preview requirements from target behavior;
   - define email-bound resolution as the accepted behavior;
   - require verified authenticated email before pending invitation resolution;
   - define the onboarding lookup and accept-by-email APIs.
2. Update `workspace-module-requirements` docs that still mention raw token/token hash.
3. Update `.agents/resources/sequence diagram/workspace/02-invite-workspace-member.puml`.
4. Update `.agents/resources/sequence diagram/workspace/03-accept-workspace-invitation.puml`.

### Phase 0 - Tests First

Workspace unit tests:

1. Invite creates `PENDING` invitation without token hash.
2. Retry email delivery for a failed `PENDING` invitation reuses the same invitation record.
3. Invite sends mailbox after invitation commit.
4. Resend provider success sets `DeliveryStatus = Sent`, `ProviderMessageId`, `LastSentAt`, and increments `SentCount`.
5. Resend provider failure keeps invitation `PENDING` and sets `DeliveryStatus = Failed`.
6. Update invitation content is rejected once the invitation has been sent to mailbox.
7. Accept-by-email rejects when authenticated email differs from invitation email.
8. Accept-by-email rejects when `EmailVerified` is false.
9. Accept-by-email enforces internal-home and external-collaboration rules at acceptance time.
10. Clicking a previously delivered email for a `REVOKED` invitation returns a revoked outcome and does not create membership.
11. Reading or accepting an invitation after `ExpiresAt` lazily transitions it to `EXPIRED` and returns an expired outcome.
12. Opening the Owner/Admin invitation list materializes overdue pending invitations as `Expired`.

Auth tests:

1. Register normal account then verify email, then Web onboarding can resolve invitations by email through Workspace API.
2. Login with verified email can fetch pending invitations through Workspace API.
3. Login/register with unverified email cannot accept invitations.
4. Legacy `RegisterInvitedRequest(Token, ...)` path is removed or explicitly rejected.

API tests:

1. `POST /api/v1/workspaces/{workspaceId}/invitations` returns `201 Created` when invitation creation and delivery both succeed.
2. `POST /api/v1/workspaces/{workspaceId}/invitations` returns `201 Created` with warning payload when invitation creation succeeds and delivery fails.
3. `POST /api/v1/workspaces/{workspaceId}/invitations` does not return `201 Created` when validation, authorization, or persistence fails.
4. Delivery retry endpoint returns success/failure for the same invitation record without creating a new one.
5. Invitation update endpoint is not exposed after mailbox send.
6. Delivery retry is rejected when `deliveryStatus` is not `Failed`.
7. Invitation resolver returns a terminal revoked/expired outcome when the target invitation is no longer valid.
8. Expired invitations transition lazily without requiring a background job.
9. Owner/Admin invitation list materializes overdue pending invitations before mapping DTOs.

Web tests:

1. Owner/Admin invite no longer reads `result.rawToken`.
2. Email action opens the login/register flow when unauthenticated and ultimately lands on `/{workspaceSlug}/home` after verified email invitation resolution.
3. Onboarding page shows pending invitations only after authenticated verified email is available.
4. Verified user clicking an email for a single matching pending workspace invite is accepted, active workspace is selected, and user is redirected to `/{workspaceSlug}/home`.
5. Unverified user clicking an email is held at verification/onboarding and is not accepted automatically.
6. After email verification succeeds, Web resumes the stored invitation callback, accepts the matching invitation, selects the workspace, and redirects to `/{workspaceSlug}/home`.
7. Login/register callback preserves workspace slug and intended home URL query context.
8. Clicking an old email for a revoked invitation shows a clear `Invitation revoked` state and does not redirect into the workspace.
9. Opening Owner/Admin invitation list shows overdue invitations as `Expired`, not stale `Pending`.

Infrastructure tests:

1. Resend request maps `from`, `to`, subject, HTML, and text body correctly.
2. Non-2xx Resend response maps to sanitized failure.
3. Logs never contain API key, token, token hash, or full email body.

### Phase 1 - Workspace Domain/Application

1. Add invitation delivery status constants.
2. Add delivery fields to `WorkspaceInvitation`.
3. Remove `RawToken` from `InviteMemberResponse`.
4. Replace token DTOs with email-bound DTOs:
   - `PendingInvitationPreviewResponse`
   - `AcceptPendingInvitationRequest`
   - `InviteMemberResponse` with `DeliveryStatus`, `DeliveryWarning`, and `EmailLanguage`
5. Add service methods:
   - `GetPendingInvitationsForEmailAsync(userId, verifiedEmail, isEmailVerified)`
   - `AcceptPendingInvitationAsync(invitationId, userId, verifiedEmail, isEmailVerified)`
6. Keep all invitation validation rules in Workspace Application layer.

### Phase 2 - Workspace Persistence

1. Add repository query by lower-cased email/status/expiry.
2. Ensure pending lookup materializes lazy expiration before returning invitations as pending.
3. Add delivery retry update path on the same invitation record.
4. Keep acceptance mutation and member creation inside one save boundary.

### Phase 3 - Workspace Mailbox and Resend

1. Add `WorkspaceInvitationMailMessage` and `InvitationMailDeliveryResult`.
2. Add `IWorkspaceInvitationMailbox`.
3. Implement `ResendWorkspaceInvitationMailbox`.
4. Compose email with:
   - workspace name;
   - inviter display information if available;
   - role and membership type;
   - expiration date;
   - `Accept & Join Workspace` link whose visible target is `${AppBaseUrl}/{workspaceSlug}/home`; if auth is required, Web redirects through login/register and preserves the workspace slug in callback context.
5. Do not include token, token hash, or invitation id as a bearer credential.
6. Support retrying delivery for an existing invitation without creating a new invitation record.

### Phase 4 - Workspace API and gRPC

HTTP API target:

- Keep: `POST /api/v1/workspaces/{workspaceId}/invitations`
- Keep: `GET /api/v1/workspaces/{workspaceId}/invitations`
- Keep: `DELETE /api/v1/workspaces/{workspaceId}/invitations/{invitationId}`
- Add: `POST /api/v1/workspaces/{workspaceId}/invitations/{invitationId}/retry-delivery`
- Add: `GET /api/v1/workspaces/invitations/pending`
- Add: `POST /api/v1/workspaces/invitations/{invitationId}/accept`
- Remove/deprecate: `GET /api/v1/workspaces/invitations/preview?token=...`
- Remove/deprecate: `POST /api/v1/workspaces/invitations/accept` with token body
- Return `201 Created` from `POST /api/v1/workspaces/{workspaceId}/invitations` when the invitation resource is created.
- Do not expose invitation content update endpoint after mailbox send.
- Allow retry-delivery only when the invitation is still `PENDING` and `deliveryStatus = Failed`.

gRPC target:

- Replace `VerifyInvitationToken(token)` with `GetPendingInvitationsByEmail(email, userId, emailVerified)`.
- Replace `AcceptInvitation(token, userId, email)` with `AcceptInvitationById(invitationId, userId, email, emailVerified)`.
- Keep service-to-service communication through gRPC only.

### Phase 5 - Auth Service

1. Remove `RegisterInvitedRequest(string Token, ...)` target contract.
2. Use normal register/login flow to authenticate the user.
3. Ensure JWT includes `email` and `email_verified` claims used by Workspace API.
4. Do not make Auth automatically resolve or accept Workspace invitations after login. Auth remains responsible for identity only.
5. Fix existing sensitive logging:
   - remove `Token: {Token}` from `RegisterInvitedAsync` error log even before the endpoint is deleted.

### Phase 6 - Web App

1. Remove `result.rawToken` usage from:
   - workspace members page;
   - workspace invitations page.
2. Remove `/invitations/[token]` target route or keep it temporarily as a migration page that redirects to tokenless onboarding with an explanatory message.
3. Update invite success UI to show delivery status/warning.
4. Keep invitation actions in UI intentionally narrow:
   - `Retry email`
   - `Revoke invitation`
   - `Create new invitation`
5. Do not allow editing invitation content after mailbox send.
6. Show `Retry email` only when `deliveryStatus = Failed`.
7. Update onboarding route to:
   - prompt login/register;
   - fetch pending invitations from Workspace API after auth and email verification;
   - auto-accept a single pending invitation matching the intended workspace slug;
   - call active workspace selection for the accepted workspace;
   - redirect to `/{workspaceSlug}/home`;
   - store and resume the safe callback when the user must verify email first;
   - show a terminal revoked/expired state when the target invitation is no longer valid;
   - show a manual pending-invitation list only when the route is ambiguous or no intended workspace is present.
8. Update auth redirect handling:
   - preserve the full safe callback path and query, not just `pathname`;
   - ensure callbacks cannot redirect to external URLs or protocol-relative URLs.

### Phase 7 - Sequence Diagram Update

`02-invite-workspace-member.puml`:

- Replace `Mailbox API` with:
  - `participant ":WorkspaceInvitationMailbox" as Mailbox <<boundary>>`
  - `participant "Resend API" as Resend <<external system>>`
  - `boundary "Invitee Mailbox" as InviteeMailbox`
- Remove raw token from messages.
- Add `alt Resend delivery succeeds / Resend delivery fails`.
- Show delivery metadata update after provider result.

`03-accept-workspace-invitation.puml`:

- Remove token parsing from mailbox.
- Remove `VerifyInvitationToken(Token)`.
- Show login/register first.
- Show email verification gate and post-verification resume.
- Show `GetPendingInvitationsByEmail`.
- Show auto-accept when a single pending invitation matches the intended workspace slug.
- Show `AcceptInvitationById`.
- Show active workspace selection.
- Show redirect to `/{workspaceSlug}/home`.
- Show terminal `Invitation revoked` / `Invitation expired` outcome when the email link no longer maps to a valid pending invitation.

### Phase 8 - Verification

Run from `warptalk-backend`:

```powershell
dotnet test workspace/tests/WarpTalk.WorkspaceService.Tests/WarpTalk.WorkspaceService.Tests.csproj
dotnet test auth/tests/WarpTalk.AuthService.Tests/WarpTalk.AuthService.Tests.csproj
dotnet build workspace/src/WarpTalk.WorkspaceService.API/WarpTalk.WorkspaceService.API.csproj
dotnet build auth/src/WarpTalk.AuthService.API/WarpTalk.AuthService.API.csproj
```

Run from `warptalk-web`:

```powershell
npm run lint
npm run build
```

Manual smoke test:

1. Set `RESEND__APIKEY`.
2. Invite an email from Workspace Members page.
3. Verify DB has a `PENDING` invitation with no token hash.
4. Verify email arrives with no tokenized URL.
5. Click the email button while logged out and register/login using the same email.
6. Verify pending invitation is accepted only after email is verified.
7. Verify unverified users resume the invitation callback after successful verification.
8. Verify active workspace is selected and user lands directly on `/{workspaceSlug}/home`.
9. Retry failed delivery and confirm the same invitation remains `PENDING` with updated delivery metadata.
10. Attempt to update invitation content after mailbox send and confirm the request is rejected or the action is unavailable in UI.
11. Attempt to retry delivery for a `Sent` invitation and confirm the action is rejected or hidden.
12. Revoke a previously emailed invitation, click the old email link, and verify the UI shows `Invitation revoked` without creating membership or selecting a workspace.
13. Open an expired invitation link and verify the system materializes `EXPIRED` lazily and shows `Invitation expired`.
14. Open Owner/Admin invitation list containing overdue pending invitations and verify the API/UI materializes and shows `Expired`.

## Risks and Mitigations

- **Account takeover by unverified email**: require `EmailVerified = true` before preview/accept.
- **Provider outage**: keep invitation pending and return delivery warning.
- **Spec drift**: update WT-140 spec and workspace-module docs before code.
- **Breaking existing frontend route**: migrate `/invitations/[token]` deliberately; do not leave it as a functional token path.
- **gRPC contract breakage**: update shared proto, regenerate clients, and run Auth + Workspace tests together.
- **Provider lock-in**: keep Resend in shared infrastructure behind `IResendEmailClient`.

## Acceptance Criteria

- Owner/Admin invite creates a pending invitation and sends an email through Resend.
- No raw invitation token is generated, stored, returned, logged, or embedded in email links.
- Pending invitation preview/accept is based on authenticated verified email.
- Email failure does not roll back invitation creation.
- Delivery retry does not create a new invitation.
- Invitation content cannot be updated in place after mailbox send.
- Delivery retry is allowed only for `PENDING` invitations whose latest `deliveryStatus` is `Failed`.
- Delivery status is persisted and exposed in invite response/listing.
- Clicking an old email for a revoked invitation produces a clear terminal revoked state and never creates membership.
- Expiration is handled lazily on resolver/read/accept and does not require a background job in this phase.
- Owner/Admin invitation list materializes overdue pending invitations as `Expired` before returning data.
- Auth, Workspace, Web, specs, and sequence diagrams all reflect email-bound invitation resolution.
- Existing role/domain/membership rules still pass.
