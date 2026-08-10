# Feature Specification: WT-333 My Meetings

**Feature Branch**: `feat/wt-333-my-meetings`
**Created**: 2026-08-10
**Status**: approved
**Input**: Coordinator SDD audit for WT-333 / UC 25

> Audit note: implementation and automated tests already existed in the worktree before this SDD audit found the missing feature spec. This draft captures the implemented contract for coordinator/user review before commit, push, or PR.

## Problem Statement

Workspace Owner/Admin users need a personal "My Meetings" view that shows only meetings they personally host, participate in, or are invited to within one workspace. The existing workspace listing/history behavior intentionally widens Owner/Admin visibility to every room in the workspace, which is correct for administration but wrong for a personal timeline.

Without a dedicated personal route, an Owner/Admin opening "My Meetings" can see tenant-wide workspace rooms instead of their own meetings, and users invited to upcoming meetings have no single past-and-upcoming timeline.

## User Story

As a workspace user, I want to load my own meetings in one workspace, including past and upcoming rooms, so that I can navigate only the meetings that involve me without seeing the entire tenant's administrative room list.

## Functional Requirements

* **FR-333-001**: The system MUST expose `GET /api/v1/translation-rooms/my-meetings`.
* **FR-333-002**: The endpoint MUST require an authenticated caller.
* **FR-333-003**: The endpoint MUST require `WorkspaceId`; missing or empty workspace scope MUST fail validation.
* **FR-333-004**: The endpoint MUST return only rooms readable by the caller through the translation-room database facts: host, participant, or active invitation by email.
* **FR-333-005**: Workspace Owner/Admin widening MUST NOT apply to the personal timeline.
* **FR-333-006**: The endpoint MUST still apply the requested workspace boundary and MUST NOT leak the caller's own rooms from another workspace.
* **FR-333-007**: When no status filter is supplied, the endpoint MUST include both past and upcoming active rooms instead of defaulting to history-only ended/cancelled statuses.
* **FR-333-008**: Returned rooms MUST be ordered by the implemented personal timeline order: newest scheduled/booked slot first, falling back to started, ended, then created timestamps.
* **FR-333-009**: The response shape MUST match `TranslationRoomHistoryResponse`, including room list item, participants, artifacts, pagination metadata, and total count.
* **FR-333-010**: Artifact content MUST remain governed by per-room `ArtifactAccess`; listing a room MUST NOT widen artifact body visibility.
* **FR-333-011**: The existing workspace active list and history endpoints MUST retain Owner/Admin workspace-wide visibility behavior.
* **FR-333-012**: The personal timeline participant lookup SHOULD be supported by an index on `translation_room.translation_room_participants(user_id)`.

## Acceptance Criteria

1. Given a workspace Admin hosts one room and another unrelated workspace room exists, when the Admin calls My Meetings, then only the Admin's own room is returned.
2. Given a user is invited by email to an upcoming scheduled room and has not joined, when the user calls My Meetings for that workspace, then the upcoming room is returned.
3. Given a participant can list a host-only artifact room, when My Meetings returns the room, then artifact metadata is present but artifact content is withheld.
4. Given the host calls My Meetings for the same host-only artifact room, then artifact content is included.
5. Given an upcoming scheduled room and a past ended room both involve the caller, when My Meetings is loaded, then the upcoming room is ordered ahead according to descending scheduled timeline order.
6. Given the caller owns rooms in another workspace, when My Meetings is loaded for the selected workspace, then other-workspace rooms are not returned.
7. Given no WorkspaceId is supplied, when My Meetings is called, then the service returns validation failure.
8. Given a workspace Owner/Admin loads the existing active list or room history, when there are rooms they do not personally participate in, then workspace-wide admin visibility remains intact.

## Out of Scope

* Cross-workspace personal timeline aggregation.
* Changing room detail authorization.
* Changing workspace Owner/Admin behavior for administrative active list/history endpoints.
* Changing artifact download authorization.
* UI implementation.

## Verification

Automated evidence already collected in this worktree:

* `dotnet build translation-room/tests/WarpTalk.TranslationRoomService.Tests/WarpTalk.TranslationRoomService.Tests.csproj --no-restore -v:minimal` passed with 0 warnings and 0 errors.
* `dotnet test translation-room/tests/WarpTalk.TranslationRoomService.Tests/WarpTalk.TranslationRoomService.Tests.csproj --filter "FullyQualifiedName~WorkspaceAdminRoomVisibilityTests.MyMeetings_ExcludesWorkspaceRooms_TheAdminIsNoPartOf" --no-build ...` passed 1/1.
* `dotnet test translation-room/tests/WarpTalk.TranslationRoomService.Tests/WarpTalk.TranslationRoomService.Tests.csproj --filter "FullyQualifiedName~WorkspaceAdminRoomVisibilityTests" --no-build ...` passed 12/12.
* `dotnet test translation-room/tests/WarpTalk.TranslationRoomService.Tests/WarpTalk.TranslationRoomService.Tests.csproj --no-build ...` passed 417/417.

Runtime Docker verification is still pending and MUST be completed after this draft spec is approved, before PR creation.

## Approval

Approved by the user on 2026-08-10 after the coordinator surfaced the missing-spec audit.
