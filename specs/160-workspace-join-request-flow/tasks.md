# Tasks: Workspace Join Request Flow

Branch: `feat/workspace-join-request-flow`

## Phase 0 — Contract and tests first

- [x] Add/adjust backend service tests for provisional membership classification, Member-only approval, atomic invitation/member creation, duplicate requests, and invalid transitions.
- [x] Add backend controller contract tests for create, list/status, approve, and reject Join Request endpoints.
- [x] Add type-safe web request/response wiring for Hub submit/pending/approved states and Owner/Admin tab actions.
- [x] Confirm the infrastructure migration is idempotent and leaves existing outbound invitation rows compatible.

## Phase 1 — Domain and persistence

- [x] Add `InvitationStatus.REJECTED`.
- [x] Add `RequestedBy`, `ReviewedBy`, and `ReviewedAt` to `WorkspaceInvitation` and EF mapping/DTO mapping.
- [x] Add the migration, nullable auth-user foreign keys, requested-by backfill, and Join Request indexes.
- [x] Preserve the existing active-member uniqueness behavior; reject approval when an active member already exists.

## Phase 2 — Application and API

- [x] Keep Join Request creation on `REQUESTED` with provisional Internal/External classification.
- [x] Add approval membership-type input validation and transactionally update invitation plus insert a new Member record.
- [x] Change rejection to `REJECTED` and persist reviewer tracking.
- [x] Add user-scoped Join Request status endpoint for the Hub.
- [x] Return approval email delivery outcome without making email delivery part of the membership transaction.

## Phase 3 — Email and infrastructure integration

- [x] Reuse the existing Resend email client/composer for post-approval email.
- [x] Add/update the approval email template and workspace-home link.
- [x] Keep infrastructure compose changes already present; add only the migration/contract needed by this feature.
- [x] Verify the AI repository has no Join Request contract consumer; leave it unchanged if none exists.

## Phase 4 — Web experience

- [x] Submit Join Workspace with slug through the API and show a pending approval state; do not switch workspace immediately.
- [x] Show current memberships and Join Request statuses independently on Workspace Hub.
- [x] Add Owner/Admin-only Invitations entry to Linear sidebar under the workspace slug.
- [x] Implement Invitations and Join Requests pill tabs, including approve/reject and final Internal/External selection.
- [x] Use the existing Select Workspace/session flow for `Open Workspace A` after approval.
- [x] Hide Admin role assignment from Join Request approval; keep existing member-role management unchanged.

## Phase 5 — Verification

- [x] Run backend API build and inspect migration/model diffs.
- [x] Run offline TypeScript/TSX parser checks for all changed web routes/hooks/services.
- [x] Recheck all four repository branches and confirm unrelated AI/infrastructure changes were preserved.
- [x] Run the full Workspace test project after its pre-existing `DocumentSecurityGuardrailConsumerServiceTests` namespace errors are resolved (166/166, including Testcontainers integration).
- [x] Run frontend lint/build after restoring dependency metadata; direct smoke checks for `/workspace/join`, `/workspace/invitations`, and `/{workspaceSlug}/invitations` all returned 200. The legacy dashboard route script still expects the removed `/participant/meetings` route.
