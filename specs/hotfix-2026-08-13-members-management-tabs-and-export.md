# Hotfix: Members Management Tabs And Export
Date: 2026-08-13
Reporter: Workspace owner/admin UI review

## Bug
The workspace Members page merges active members, pending outbound invitations, and inbound join requests into one directory. This makes the All tab show invited/requested people who are not active workspace members, exposes an Owner tab that should not exist, lets export include pending invitation/request rows, and makes invitations/join requests look like member rows instead of CRUD queues.

## Root Cause
The frontend directory model treats invitations and join requests as rows in the member table. Backend membership-type and join-request approval rules on `development` already follow WT-140 and WT-160: invitations persist the requested membership type, public-domain internal invites are rejected when verified-domain enforcement is on, and join-request approval accepts a final membership type.

## Fix
Keep active workspace members as the member directory, sorted by role level. Hide the Owner tab while highlighting owners in member rows. Split member display into internal and external tables. Render invitations and join requests as separate management queues with revoke/reject/approve actions, and hide export on those queue tabs.

## Verification
Add/update frontend pure tests for directory filtering, sorting, active-only All behavior, and internal/external grouping. Run the scoped frontend test suite and lint/type checks where feasible. Run existing backend workspace invitation/member tests to confirm the server-side contract still passes.

## Regression Risk
The main risk is UI state divergence between filtered tabs and fetched paginated member data. The fix should keep API contracts unchanged and avoid backend schema or behavior changes.
