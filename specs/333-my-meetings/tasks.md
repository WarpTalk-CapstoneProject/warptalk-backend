# Tasks: WT-333 My Meetings

**Status**: approved
**Spec**: `specs/333-my-meetings/spec.md`

> Audit note: these tasks reflect work already present before the missing-spec audit. They are recorded now so the branch can be reviewed against SDD before any commit/push/PR.

## Phase 0: Tests First / Regression Coverage

* [x] Add regression test: Admin My Meetings excludes workspace rooms the Admin is not part of.
* [x] Add regression test: My Meetings includes upcoming invited room before the invitee joins.
* [x] Add regression test: My Meetings withholds artifact content from participant for host-only room.
* [x] Add regression test: My Meetings returns artifact content to host.
* [x] Add regression test: My Meetings orders by descending scheduled timeline order.
* [x] Add regression test: My Meetings does not leak own rooms from another workspace.
* [x] Add regression test: My Meetings rejects missing WorkspaceId.
* [x] Preserve existing workspace Admin list/history visibility tests.

## Phase 1: Application Contract

* [x] Add `GetMyMeetingsAsync` to `ITranslationRoomService`.
* [x] Implement workspace-required validation.
* [x] Reuse room history response shape for the personal timeline.

## Phase 2: Query Behavior

* [x] Extract shared timeline page builder.
* [x] Add private `RoomTimelineScope` to distinguish workspace-admin archive from personal timeline.
* [x] Force My Meetings to use host/participant/invitation read boundary.
* [x] Keep history default statuses as `ENDED,CANCELLED`.
* [x] Default My Meetings statuses from the declared `RoomStatus` enum so frontend `upcoming`, `live`, and `past` buckets stay aligned with the backend lifecycle set.
* [x] Use descending scheduled timeline order and document it consistently.

## Phase 3: API Layer

* [x] Add `GET /api/v1/translation-rooms/my-meetings`.
* [x] Map service failures to existing API error response/status conventions.

## Phase 4: Database / Performance

* [x] Add migration `translation-room/database/migrations/20260810070000_add_participants_user_id_index.sql`.
* [x] Use `CREATE INDEX IF NOT EXISTS translation_room_participants_user_id_idx ON translation_room.translation_room_participants (user_id)`.

## Phase 5: Local Verification

* [x] `git diff --check` passed.
* [x] Targeted build passed.
* [x] Targeted WT-333 test passed.
* [x] Full relevant visibility suite passed 12/12.
* [x] Full TranslationRoom test project passed 417/417.
* [ ] Runtime Docker rebuild from `C:\Users\Admin\wt333-backend` completed.
* [ ] Runtime My Meetings API flow verified.

## Phase 6: PR Prep

* [x] Coordinator/user approved the recovered spec on 2026-08-10.
* [ ] Re-run necessary gates after any approval edits.
* [ ] Commit with `Ngô Xuân Hạnh Nhi <hanhnhi10022005@gmail.com>`.
* [ ] Push branch.
* [ ] Open PR into `development`.
* [ ] Check CI and fix any failures.
