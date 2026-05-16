# WT-67: 1.5 Build Room Lifecycle Controls

## 1. Description
Implement actual room lifecycle actions and legal state transitions.

## 2. Implementation Scope
* Add start room action (MUST activate runtime audio routing by transitioning related routes to `AUDIO_ROUTING_ACTIVE`).
* Add pause room action.
* Add resume room action.
* Add end room action.
* Add cancel room action.
* Add expire room handling.
* Update timestamps: `started_at`, `ended_at`, `duration_seconds`.
* Block illegal transitions.

## 3. Acceptance Criteria
* `SCHEDULED -> WAITING` works.
* `WAITING -> IN_PROGRESS` works.
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
2. **Given** a waiting room, **When** the host starts the session, **Then** status changes to `IN_PROGRESS`, `started_at` is set, and associated audio routes transition to `AUDIO_ROUTING_ACTIVE`.
3. **Given** an in-progress room, **When** the host ends the session, **Then** status changes to `ENDED`, `ended_at` is set, and `duration_seconds` is calculated.
4. **Given** an illegal transition, **When** the action is requested, **Then** the system rejects it without changing the room state.

**Functional Requirements**:

* **FR-1.5-001**: System MUST enforce the approved room lifecycle states and transitions.
* **FR-1.5-002**: System MUST update lifecycle timestamps consistently when rooms start, end, cancel, or expire.
* **FR-1.5-002b**: System MUST broadcast `AUDIO_ROUTING_ACTIVE` to relevant audio routes when the room successfully starts.
* **FR-1.5-003**: System MUST reject illegal transitions with clear error responses.
* **FR-1.5-004**: System MUST not preserve discarded draft rooms as lifecycle records.

**Key Entities**: `translation_room.translation_rooms`.

**Success Criteria**:
* All legal transitions work exactly as defined.
* Illegal transitions never mutate persisted room state.
* Tests cover every valid transition and representative invalid transitions.




