# WT-339: Translation Lifecycle Must Not Start On Room Open

## Status
Approved bug-fix scope for `fix/wt-339-translation-lifecycle`.

## Bug
Opening a translation room moved audio routes from configured/ready into `BROADCASTING`.
Downstream workers treat `BROADCASTING` and the resulting `AUDIO_ROUTES_UPDATED` signal as
"translation is active", so the AI pipeline could start transcribing and billing before the host
pressed Start Translation.

## Root Cause
`PublishRouteReadinessAsync` always emitted both route lifecycle events:

* `config_ready`: routes have been generated and are ready to wait.
* `session_starts`: translation is actively running on those routes.

Those events were coupled in the room-open path, late-join path, and idempotent restart path. The
service therefore treated "room is open / LiveKit meeting is available" as equivalent to
"translation session is active".

## New Lifecycle Contract
This spec supersedes and refines `specs/067-build-room-lifecycle-controls/spec.md`
`FR-1.5-002b` for WT-339 behavior.

* Room open/start (`StartTranslationRoomAsync`, `WAITING -> IN_PROGRESS`) MUST generate/configure
  routes and emit `config_ready` only when no active `TranslationRoomSession` exists.
* Start Translation / resume (`ResumeTranslationRoomAsync`) MUST open an active
  `TranslationRoomSession` when none exists, then allow routes to receive `session_starts` and
  transition to `BROADCASTING`.
* Late join into an open room with no active translation session MUST configure the participant's
  routes but MUST NOT broadcast them.
* Late join into a room with an active translation session MUST configure and broadcast the new
  participant's routes.
* Retrying Start Translation while an active translation session already exists MUST be idempotent
  and MUST NOT create duplicate active sessions.
* Concurrent Start Translation retries MUST serialize per room and persist exactly one active
  `TranslationRoomSession`, including when requests are handled by different service instances.

## Acceptance Criteria
* Opening a waiting room succeeds and leaves routes at READY/configured, not `BROADCASTING`.
* Opening a room does not create a `TranslationRoomSession`.
* Pressing Start Translation on an open `IN_PROGRESS` room creates one active
  `TranslationRoomSession` and emits the route events needed to reach `BROADCASTING`.
* Retrying Start Translation with an active session does not create a duplicate active session.
* Two concurrent Start Translation requests both succeed and leave exactly one active session.
* A participant joining before Start Translation does not start translation for the room.
* A participant joining after Start Translation receives broadcasting routes.
* Unauthorized users still cannot start/resume translation.
* Illegal lifecycle transitions still return clear errors without mutating persisted state.

## Runtime Verification
Docker-backed verification must rebuild the `translation-room` service from this worktree and
exercise:

* Main bug flow: open room, confirm route readiness without `BROADCASTING` and no active session.
* Translation-start flow: call Start Translation/resume, confirm one active session and routes
  transition to `BROADCASTING`.
* Regression flow: late join before Start Translation stays READY; late join after Start
  Translation reaches `BROADCASTING`.
* Error/permission flow: non-host or unauthorized caller cannot start/resume translation.

Evidence must include request/response, server log excerpts, and DB state for room status, active
session count, and relevant route status.

## Regression Coverage
Unit coverage must pin:

* `StartTranslationRoomAsync` emits `config_ready` without `session_starts` when no active session
  exists.
* `StartTranslationRoomAsync` does not add a `TranslationRoomSession`.
* `ResumeTranslationRoomAsync` opens a session, emits `session_starts`, and sends `room_resume`.
* `ResumeTranslationRoomAsync` is idempotent when an active session already exists.
* Docker-backed integration coverage sends concurrent Start Translation requests and asserts one
  active session in PostgreSQL.
* `JoinTranslationRoomAsync` distinguishes open-room READY from active-translation BROADCASTING.

## Risk Notes
This is an intentional refinement of the older WT-67 coupling between room lifecycle and route
broadcasting. Room status `IN_PROGRESS` can now mean "meeting open" while translation activation is
represented by the presence of an active `TranslationRoomSession`.
