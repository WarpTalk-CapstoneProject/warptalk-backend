# WT-158 Tasks: External Document Upload 401

## Phase 0 - Regression Signal

- [x] Reproduce External upload through Gateway and prove the durable result is
  `pending_approval`.
- [x] Correlate the first `401`, refresh-token rotation, and successful document
  insert by timestamp.
- [x] Add a failing regression test for stale and expiring access-token
  selection.

## Phase 1 - Domain

- [x] Confirm no document lifecycle rule or schema change is required.

## Phase 2 - Backend Boundaries

- [x] Confirm authenticated External Members are authorized and receive the
  expected pending lifecycle response.
- [x] Preserve the existing Gateway and Workspace authentication policies; no
  backend production change is required.

## Phase 3 - Web Token Lifecycle

- [x] Select the newest access token across Zustand, persisted storage, and the
  cookie.
- [x] Refresh access tokens before protected requests when they are expired or
  within the refresh window.
- [x] Serialize refresh-token rotation within and across browser tabs while
  retaining the response-401 retry as a fallback.

## Phase 4 - Verification and Cleanup

- [x] Pass token lifecycle regression tests and ESLint.
- [x] Pass a clean production Docker build.
- [x] Run UI upload with an expired token and observe only
  `POST /documents = 200`, returning `pending_approval` and
  `awaiting_approval`.
- [x] Remove temporary harnesses, container, and uploaded test document.
- [x] Record the confirmed root cause and residual risks.

## Document Policy Follow-up

- [x] Keep `WorkspaceDocumentAccessPolicy` as sparse per-document overrides.
- [x] Validate supported subjects, permissions, effects, and duplicate rules.
- [x] Enforce deny-overrides after hard lifecycle/security guards.
- [x] Keep `AllowExternalLlm = true` as a product invariant while gating AI by
  approval, guardrails, ingestion status, and `AiEligible`.
- [x] Publish document lifecycle events and invalidate the exact workspace
  list/detail query keys.
- [x] Authorize SignalR workspace-group subscriptions through Workspace gRPC
  membership validation.
