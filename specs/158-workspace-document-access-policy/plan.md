# WT-158 Implementation Plan: External Document Upload 401

## Scope

Fix the authenticated External Member upload flow so a valid
`POST /api/v1/workspaces/{workspaceId}/documents` request returns the durable
upload result instead of `401 Unauthorized`.

## Architecture Gates

- Keep authentication at Gateway/API boundaries.
- Keep membership and document lifecycle rules in Workspace Application.
- Preserve `401` for missing/invalid identity and `403` for authenticated users
  without workspace permission.
- Do not change the document schema for this fix.
- Add regression coverage before changing production behavior.

## Approach

1. Reproduce the request through Gateway using a real External membership.
2. Capture the status and authentication state at Gateway and Workspace API.
3. Minimize the failure into an automated regression test at the closest real
   request boundary.
4. Fix the root cause without weakening JWT validation.
5. Verify External upload returns success with `pending_approval`, while invalid
   tokens still return `401` and non-members return `403`.
6. Verify the web client does not reuse stale authentication/workspace context
   during upload.

## Confirmed Root Cause

The Workspace API authorization policy is not rejecting External Members.
Direct External upload returns `200` with `pending_approval`.

The browser held an expired access token. The original multipart request reached
Gateway and returned `401`; the Axios response interceptor then rotated the
refresh token and replayed the request successfully. The document was inserted
immediately after refresh, which explains why the UI reported a failed resource
while the database contained a pending document.

The response retry was functionally successful but too late to prevent Chrome
from reporting the initial `401`. Stale copies of access and refresh tokens
across the in-memory store, persisted state, cookie, and concurrent tabs also
made the flow vulnerable to token-rotation races.

## Implemented Fix

- Choose the newest access token from all client-side token sources.
- Refresh an expired or nearly expired token before sending protected requests.
- Use one refresh promise per tab and the Web Locks API across tabs so a rotating
  refresh token is consumed once.
- Keep the existing response-401 retry as a fallback without weakening Gateway
  JWT validation.
- Add focused token-lifecycle regression tests and verify the production UI
  upload path with an intentionally expired token.

## Risks

- Token refresh may replay multipart requests.
- Multiple cookies with the same name may select a stale token.
- Fixing External upload must not let unauthenticated or removed users upload.
- A failed response after a durable save can encourage duplicate uploads.
- Browsers without Web Locks still receive same-tab single-flight protection,
  but cannot coordinate refresh-token rotation across different tabs.

## Follow-up Architecture Decision: Sparse Document Policy Overrides

`WorkspaceDocumentAccessPolicy` remains part of the document module. It is not a
general Workspace RBAC table and must not contain a precomputed matrix for every
role, membership type, document, and permission.

The table stores only explicit per-document exceptions:

- `User`, `Role`, or `MembershipType` subject.
- `view`, `download`, or `ai_retrieval` permission.
- `ALLOW` or `DENY` effect.

Evaluation order:

1. Enforce hard membership, lifecycle, retention, approval, ingestion, and AI
   eligibility guards. Explicit ALLOW cannot bypass these guards.
2. Apply all matching explicit DENY rows.
3. Apply matching explicit ALLOW rows.
4. If no override decides the request, apply dynamic defaults from role,
   membership type, document ownership, confidentiality, and meeting
   participant grace period.

`AllowExternalLlm` is always `true` by product invariant because WarpTalk has no
local model. It is a provider capability, not a document authorization grant.
AI ingestion/retrieval still requires approved/public status, completed
guardrails, and `AiEligible = true`.
