# WT-346: Select And Switch Active Workspace Safely

## Status
Approved by the user on 2026-08-10.

## Bug
Selecting a workspace could cache an unusable or incorrect active workspace context. A stale
membership row could select a missing, soft-deleted, or deactivated workspace, and recalculating
membership type from the user's current email/domain could differ from the membership classification
stored when access was granted. Non-members also received a response that disclosed the workspace
exists.

## Contract

* `POST /api/v1/workspaces/{id}/select` MUST succeed only for an active membership whose
  `RemovedAt` is null.
* Missing, suspended, or removed membership, missing workspace, and soft-deleted workspace MUST
  return `404 Not Found` and MUST NOT update the active-workspace cache.
* A deactivated workspace MUST return `404 Not Found` with a clear inactive-workspace error and MUST
  NOT update the active-workspace cache.
* Successful selection MUST cache the member's role and the `MembershipType` stored on the
  membership row; it MUST NOT recalculate membership type from email/domain state.
* `GET /api/v1/workspaces/{id}` MUST return `404 Not Found` to non-members rather than disclose that
  an inaccessible workspace exists.

## Acceptance Criteria

* An active internal member can select the workspace and Redis receives `Internal`.
* An active external member can select the workspace and Redis receives `External`.
* A non-member receives 404 from both get-by-id and select endpoints.
* Missing, soft-deleted, and inactive workspaces are not cached as active context.
* Existing successful response shape remains unchanged.

## Verification

* Unit tests cover internal/external cache values and all rejection paths.
* Controller tests pin 404 mapping for hidden workspaces.
* Docker-backed integration verifies authorized selection, non-member privacy, inactive workspace
  rejection, and Redis active-context mutation/non-mutation.
