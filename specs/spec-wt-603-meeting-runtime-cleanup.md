# WT-603: Meeting Runtime Cleanup

## Overview and Goals

Linear: https://linear.app/fpt-sep490-su26/issue/WT-603/spikemeeting-refactor-meeting-runtime-naming-and-remove-dead

`meeting-service` owns runtime state for live meetings in PostgreSQL database
`warptalk_meeting`, schema `meeting`. This ticket removes Polls, Q&A, and
Breakouts, which are no longer part of the product, and aligns C# RTC entity
names with the physical tables already deployed in local and production.

The business meeting and booking source of truth remains
`warptalk_translation_room.translation_room.translation_rooms`.

## Scope and User Stories

Affected roles: Host and Participant.

- As a host or participant, I can continue joining a live room, seeing RTC
  participants, chatting, recording, and using transcripts after the cleanup.
- As an operator, I can apply one versioned migration to the same logical
  database name in local and production.
- As a developer, C# types describe RTC runtime records without implying that
  they are the business meeting roster or reusable invitations.

In scope:

- Remove Polls, Q&A, and Breakouts API, application, domain, persistence,
  worker, frontend, and test surfaces.
- Rename `MeetingParticipant` to `RtcStreamParticipant` and
  `MeetingInvitation` to `RtcSessionRevocation`, including repositories,
  navigation properties, and unit-of-work accessors.
- Preserve `meeting.rtc_stream_participants` and
  `meeting.rtc_session_revocations` physical table names.
- Add a service-owned migration that removes the retired feature tables and is
  staged byte-for-byte in the infrastructure release artifact.
- Keep local and production database/schema names as `warptalk_meeting` and
  `meeting`.

Out of scope:

- Renaming the database, schema, service, container image, or gateway route.
- Moving chat, recording, transcript integration, or LiveKit runtime state.
- Renaming the existing `meeting_tracks.meeting_participant_id` column. This
  legacy physical column can be handled by a later expand-and-contract change.

## Functional and UI Specifications

- Removed Polls, Q&A, and Breakouts routes must no longer be registered by the
  Meeting Service.
- Removed live-room panels, hooks, API wrappers, realtime subscriptions, and
  setup dialogs must no longer ship in the web application.
- Join, participant presence, host transfer, kick/revocation, chat, recording,
  and transcript UI behavior must remain available.
- No replacement UI is introduced for the removed features.

## Data and API Contracts

- Database: `warptalk_meeting` in local and production.
- Schema: `meeting`.
- Preserved tables include `meeting_rooms`, `rtc_stream_participants`,
  `rtc_session_revocations`, `meeting_tracks`, and all `meeting_chat_*` tables.
- Removed tables are `poll_votes`, `poll_options`, `polls`, `question_votes`,
  `questions`, `breakout_assignments`, and `breakout_sessions`.
- The canonical migration lives under `meeting/database/migrations` in the
  backend repo. `scripts/collect-service-migrations.sh` must stage an identical
  copy under `scripts/service-migrations/meeting` in infrastructure.
- Local and production both execute
  `scripts/run-logical-database-migrations.sh`; production invokes it only via
  the immutable release workflow.

## Acceptance Criteria

1. Meeting Service builds and its retained tests pass.
2. Web typecheck/build and retained live-room contract tests pass.
3. No source reference remains to removed Polls, Q&A, or Breakouts product
   surfaces, except migration history and explicit absence contract tests.
4. No C# type or repository named `MeetingParticipant` or `MeetingInvitation`
   remains in Meeting Service production or test code.
5. EF Core still maps `RtcStreamParticipant` and `RtcSessionRevocation` to the
   existing RTC table names without a table rename.
6. The new Meeting migration is identical in backend source and infrastructure
   staging and is discovered by the logical-database migration runner.
7. Local and production compose configuration still points meeting-service to
   `warptalk_meeting` with search path `meeting,public`.
8. The production rollout requires a preflight row-count/export decision before
   the destructive migration and verifies the migration ledger plus absence of
   all seven tables after deployment.

## Edge Cases and Non-Functional Requirements

- The migration drops child tables before parent tables and is idempotent under
  the repository migration runner.
- A production backup/export is required if any retired table contains rows.
- Migration failure must stop deployment; it must never be bypassed by a direct
  production SSH deployment.
- Existing dirty documentation/diagram work in developer worktrees must be
  preserved and not reverted by this refactor.
