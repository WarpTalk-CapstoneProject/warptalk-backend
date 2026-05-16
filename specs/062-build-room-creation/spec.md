# Feature Specification: 1.1 & 1.2 Build Room Creation and Access Flow

**Feature Branch**: `feature/translation-room`  
**Created**: 2026-05-13  
**Status**: Completed  
**Input**: Linear Ticket WT-62, WT-63

## Description

Implement the backend feature for creating instant and scheduled translation rooms, as well as the flow for joining a translation room as a host or member.

## User Scenarios & Testing *(mandatory)*

**User Story (WT-62)**: As a Host, I want to create an instant or scheduled translation room so that participants have a valid room context before translation starts.
**User Story (WT-63)**: As a Participant, I want to join a valid translation room so that I can participate in the realtime session.

**Independent Test**: Can be tested independently by calling the create room API with valid and invalid payloads to generate a room code, and subsequently calling the join API as host/member to verify participant record creation or rejection.

**Acceptance Scenarios**:

1. **Given** a host submits a valid scheduled room request, **When** the API processes the request, **Then** the system persists a room with status `SCHEDULED` and a unique room code.
2. **Given** a host submits a valid instant room request, **When** the API processes the request, **Then** the system persists a room with status `WAITING` and returns the room context.
3. **Given** a valid room code and joinable room status, **When** a participant joins, **Then** the system creates or updates a participant record and returns room plus participant context.
4. **Given** a room is `ENDED`, `CANCELLED`, `FAILED`, or `EXPIRED`, **When** a participant tries to join, **Then** the system rejects the join with a clear error.

## Requirements *(mandatory)*

### Business Rules & Constraints

- **BR-001**: A participant can only join a room that exists and is not in a terminal state (`ENDED`, `CANCELLED`, `EXPIRED`, `FAILED`).
- **BR-002**: If the joining user is the host, they bypass approval and enter as `CONNECTED` with the `HOST` role.
- **BR-003**: The system must create a new participant record if the user is joining for the first time, or update their existing record context (`DisplayName`, `ListenLanguage`, `SpeakLanguage`) if they are re-joining.
- **BR-004**: For non-host participants (whether new, or returning from `REJECTED`, `DISCONNECTED`, or `LEFT` states), their join status is strictly determined by the room's `requires_approval` setting. If true, they enter as `WAITING`; if false, they enter as `CONNECTED`.
- **BR-005**: A user whose previous participant status is `KICKED` is permanently blocked from re-joining the room.
- **BR-006**: Room settings can only be updated by the host when the room is in `SCHEDULED` or `WAITING` status. Once the room is `IN_PROGRESS`, settings are locked. Any participant joining after a settings change must follow the newest settings.
- **BR-007**: The API must return the comprehensive context of both the room and the participant upon a successful join.

### Functional Requirements

- **FR-1.1-001**: System MUST create scheduled and instant translation rooms for an authenticated host.
- **FR-1.1-002**: System MUST validate workspace, host, title, source language, target languages, max participants, and schedule time before persistence.
- **FR-1.1-003**: System MUST generate a unique `translation_room_code` (VARCHAR 12) and prevent code collision.
- **FR-1.1-004**: System MUST set initial status according to room type: `SCHEDULED` for scheduled rooms and `WAITING` for instant rooms.
- **FR-1.2-001**: System MUST fulfill all business rules (BR-001 to BR-007) during the join flow.
- **FR-1.3-001**: System MUST provide an API endpoint (`PUT /api/v1/translation-rooms/{id}/settings`) for the host to update room settings.
- **FR-1.3-002**: System MUST implement `UpdateTranslationRoomSettingsAsync` in the application layer to enforce BR-006 before persisting the new settings to the database.

### Technical Constraints & Standards

- **TC-001**: Must adhere to all established code conventions and system architecture.
- **TC-002**: Code must follow the current Clean Architecture standards of the system.
- **TC-003**: Must use pure EF Core scaffold for Domain Entities and DbContext. Do not manually modify scaffolded Entities or DbContext files during coding. Database schema dictates the models.

### Key Entities

- `translation_room.translation_rooms`
- `translation_room.translation_room_participants`

## Success Criteria *(mandatory)*

- Valid create requests return a room ID/code.
- Valid participants can join active/waiting rooms.
- Closed or invalid rooms cannot be joined.
- Automated tests cover scheduled creation, instant creation, validation failure, valid join, and closed room scenarios.

## Technical Debt

- **TD-001**: Validation for `max_participants` limit based on workspace subscription tiers.
- **TD-002**: Validation for the maximum allowed scheduling time in the future for a `SCHEDULED` room.

## Implementation Notes

- Fully implemented the Create Translation Room and Join Translation Room flows with DTOs, Mappers, and Service Logic.
- Adheres strictly to Clean Architecture. The Presentation layer (gRPC/Controllers) correctly routes through the Application layer (`ITranslationRoomService`) without leaking Infrastructure implementation details.
- Integrated `FluentValidation` to cover all edge cases with AutoValidation globally enabled in `Program.cs`.
- Adopted `Enum` structures (`TranslationRoomParticipantRole`, `TranslationRoomParticipantStatus`, `RoomStatus`) rather than magic strings to enhance type safety, leveraging `HasConversion` in EF Core to ensure PostgreSQL compatibility.
- Centralized `TranslationRoomConstants` by flattening nested error definitions and aligning with project conventions (e.g., `NotificationConstants`).
- Automated Testing is fully operational for validators and helpers.
