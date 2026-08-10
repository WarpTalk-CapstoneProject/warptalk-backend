# WT-67: 1.5 Build Room Lifecycle Controls

> WT-339 reconciliation: this spec now distinguishes opening a room from starting translation.
> `StartRoom` opens the LiveKit meeting and configures audio routes to READY. Start Translation /
> Resume creates or reuses an active `TranslationRoomSession` and is the only lifecycle action that
> moves relevant routes to `BROADCASTING`. See `../339-translation-lifecycle/spec.md` for bug
> traceability.

## 1. Description
Implement actual room lifecycle actions and legal state transitions, with room availability and
translation activation represented as separate lifecycle steps.

## 2. Implementation Scope
* Add start/open room action (MUST transition the room to `IN_PROGRESS`, set `started_at`, generate related audio routes, and configure those routes to READY without creating a translation session or transitioning routes to `BROADCASTING`).
* Add Start Translation / resume action (MUST create or reuse one active `TranslationRoomSession` and transition relevant READY or PAUSED routes to `BROADCASTING`).
* Add pause room action (MUST transition routes to `PAUSED` and activate telemetry update protection on database writes to safeguard paused status).
* Add resume room action (MUST reuse the Start Translation behavior by resuming routes back to `BROADCASTING` and telemetry evaluation).
* Add end room action (MUST wind down streams via `ENDING` -> `SAVING_OUTPUTS` -> `COMPLETED` and invoke **Unified Redis Cache Cleanup** immediately to free RAM).
* Add cancel room action.
* Add expire room handling.
* Update timestamps: `started_at`, `ended_at`, `duration_seconds`.
* Block illegal transitions.

## 3. Acceptance Criteria
* `SCHEDULED -> WAITING` works.
* `WAITING -> IN_PROGRESS` opens the room and configures routes to READY without starting translation.
* Start Translation / resume from `IN_PROGRESS` or `PAUSED` creates or reuses one active translation session and moves relevant routes to `BROADCASTING`.
* `IN_PROGRESS -> PAUSED -> IN_PROGRESS` works.
* `IN_PROGRESS -> ENDED` works.
* `SCHEDULED/WAITING -> CANCELLED` works.
* `SCHEDULED/WAITING -> EXPIRED` works.
* Illegal transitions return clear error.
* Tests cover all valid and invalid transitions.

## 4. Output Acceptance (Specify)

**User Story**: As a Host, I want legal lifecycle controls for a room so that a session starts, pauses, resumes, ends, cancels, or expires consistently.

**Independent Test**: Can be tested independently by creating rooms in each allowed state, invoking lifecycle actions, and verifying resulting status and timestamps.

**Acceptance Scenarios**:

1. **Given** a scheduled room, **When** the host opens waiting mode, **Then** status changes from `SCHEDULED` to `WAITING`.
2. **Given** a waiting room, **When** the host opens the room, **Then** status changes to `IN_PROGRESS`, `started_at` is set, associated audio routes are generated/configured to READY, no `TranslationRoomSession` is created, and routes do not transition to `BROADCASTING`.
3. **Given** an open in-progress room without an active translation session, **When** the host starts translation, **Then** the system creates one active `TranslationRoomSession` and associated audio routes transition to `BROADCASTING`.
4. **Given** an in-progress or paused room with an active translation session, **When** the host retries Start Translation or resumes, **Then** the system reuses the active session and does not create duplicates.
5. **Given** an in-progress room, **When** the host ends the session, **Then** status changes to `ENDED`, `ended_at` is set, and `duration_seconds` is calculated.
6. **Given** an illegal transition, **When** the action is requested, **Then** the system rejects it without changing the room state.

**Functional Requirements**:

* **FR-1.5-001**: System MUST enforce the approved room lifecycle states and transitions.
* **FR-1.5-002**: System MUST update lifecycle timestamps consistently when rooms start, end, cancel, or expire.
* **FR-1.5-002b**: System MUST configure relevant audio routes to READY when the room is opened/started, and MUST broadcast `BROADCASTING` only when Start Translation / resume creates or reuses an active `TranslationRoomSession`.
* **FR-1.5-003**: System MUST reject illegal transitions with clear error responses.
* **FR-1.5-004**: System MUST not preserve discarded draft rooms as lifecycle records.

**Key Entities**: `translation_room.translation_rooms`.

**Success Criteria**:
* All legal transitions work exactly as defined.
* Illegal transitions never mutate persisted room state.
* Tests cover every valid transition and representative invalid transitions.


