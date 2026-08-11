# Implementation Plan: WT-333 My Meetings

**Status**: approved
**Spec**: `specs/333-my-meetings/spec.md`
**Branch**: `feat/wt-333-my-meetings`

> Audit note: this plan documents the implementation already present in the worktree before the missing-spec blocker was identified. The user approved the recovered spec on 2026-08-10.

## Architecture

* API layer adds a dedicated authenticated route: `GET /api/v1/translation-rooms/my-meetings`.
* Application interface adds `GetMyMeetingsAsync`.
* Application service reuses the room-history projection path through a shared private timeline builder.
* Domain read authorization remains centralized in `RoomReadAccess.IsReadableBy` for host, participant, and invitation visibility.
* Workspace Owner/Admin widening remains in `BuildListableRoomsQueryAsync` only for administrative list/history scope, not personal timeline scope.
* Infrastructure migration adds a participant `user_id` index for the personal timeline's "which rooms is this user in" lookup.

## Constitution / SDD Gates

* Clean Architecture: controller maps auth/request/result only; query policy stays in Application/Domain helpers; no controller business logic.
* Service boundaries: no cross-service database join introduced; workspace admin visibility still uses `IWorkspaceMemberDirectory`.
* Database: migration is additive index-only; no destructive schema/data operation.
* API contract: route remains under `/api/v1/translation-rooms`; errors use existing `ApiErrorResponse` mapping.
* Security: authenticated user identity comes from JWT claims; personal timeline uses existing host/participant/invitation read expression and requires workspace scope.
* TDD status: implementation pre-existed this audit; regression tests exist and pass, but this plan is draft until approved.

## Implementation Steps

1. Add `GetMyMeetings` controller action.
2. Add `GetMyMeetingsAsync` to `ITranslationRoomService`.
3. Extract shared timeline projection for room, participant, occupancy, artifact metadata/content filtering, pagination, and ordering.
4. Add private timeline scope enum so personal timeline can force the non-admin-widened `BuildAccessibleRoomsQuery` path.
5. Preserve history default status filter as ended/cancelled; default My Meetings to every declared `RoomStatus` value (which the frontend currently buckets into `upcoming`, `live`, and `past`) unless caller supplies status.
6. Add Testcontainers-backed regression tests for admin narrowing, invited upcoming rooms, artifact content policy, ordering, tenant boundary, missing workspace validation, and existing admin list/history behavior.
7. Add index migration on `translation_room.translation_room_participants(user_id)`.
8. Rebuild and runtime-verify Docker `translation-room-service` from `C:\Users\Admin\wt333-backend` after spec approval.

## Risk And Mitigation

* Authorization regression: mitigated by reusing `RoomReadAccess.IsReadableBy` and tests for admin narrowing plus tenant boundary.
* Administrative list regression: mitigated by tests confirming Owner/Admin workspace-wide list/history still works.
* Artifact leakage: mitigated by shared artifact projection and tests for host-only artifact content.
* Performance: mitigated by adding `translation_room_participants_user_id_idx`.
* Runtime risk: still pending Docker rebuild and API flow verification from this worktree.
