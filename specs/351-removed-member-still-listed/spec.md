# WT-351: Removed Members Must Not Reappear In Workspace Directory

## Status
Approved by the user on 2026-08-10.

## Bug
Owner/Admin member listings intentionally include suspended members, but the widened query also
included rows with `RemovedAt` set. The search branch used a separate unfiltered query, so searching
could make a removed member reappear even when the paged list hid them.

## Contract

* A removed membership is a tombstone and MUST never appear in workspace member directory results.
* Owner/Admin directory views MAY include suspended or otherwise inactive memberships, but MUST
  still require `RemovedAt IS NULL`.
* Regular-member directory views MUST include only active memberships with `RemovedAt IS NULL`.
* Search and non-search paths MUST enforce the same removal rule.
* Pagination totals MUST be calculated after removal filtering.

## Acceptance Criteria

* Owner/Admin paged listing excludes removed members and still includes suspended members.
* Owner/Admin search excludes matching removed members.
* Regular-member listing remains limited to active, non-removed members.
* A removed member cannot be recovered through pagination, search, or sort options.
* Existing authorization and email-visibility behavior remains unchanged.

## Verification

* Repository tests compile and execute the shared directory visibility predicate for regular and
  Owner/Admin views.
* Service tests cover paged and search behavior.
* Docker-backed integration seeds active, suspended, and removed rows, then verifies list/search
  responses and total counts for Owner/Admin and regular members.
