# Feature Specification: 1.3 Build Participant Management Features

**Feature Branch**: `feature/translation-room`  
**Created**: 2026-05-15  
**Status**: approved  
**Input**: Linear Ticket WT-64

## Description

Implement host controls for participants inside a translation room.

## User Scenarios & Testing *(mandatory)*

**User Story**: As a Host, I want to manage participants in a room so that the live session remains controlled and usable.

**Independent Test**: Can be tested independently by joining multiple participants, invoking participant management APIs, and verifying state, permissions, and realtime-visible results.

**Acceptance Scenarios**:

1. **Given** a host is in a room with participants, **When** the host disables or enables translation audio for a participant, **Then** the participant's audio state (`is_muted` field) changes without changing the participant lifecycle status.
2. **Given** a host kicks a participant, **When** the action succeeds, **Then** the participant status becomes `KICKED` and the participant can no longer participate in the room.
3. **Given** a participant leaves the room, **When** the action succeeds, **Then** the participant status becomes `LEFT`.
4. **Given** a non-host attempts a host-only action, **When** the request is made, **Then** the system denies the action with a 403 Forbidden.

## Requirements *(mandatory)*

### Business Rules & Constraints

- **BR-1.3-001**: System MUST provide APIs to list room participants and their current status.
- **BR-1.3-002**: System MUST allow host-only actions to enable/disable translation audio, kick, and update role/preferences.
- **BR-1.3-003**: System MUST track `CONNECTED`, `DISCONNECTED`, `LEFT`, and `KICKED` participant states.
- **BR-1.3-004**: System MUST enforce authorization (host only) for all participant management actions. Kicking a participant or disabling their translation audio can only be done by the room host.
- **BR-1.3-005**: The concept of "enable/disable translation audio" is persisted in the `is_muted` column of the participant record, and is completely separate from the participant lifecycle status.

### Technical Constraints & Standards

- **TC-001**: Must adhere to all established code conventions and system architecture.
- **TC-002**: Code must follow the current Clean Architecture standards of the system.
- **TC-003**: Must use pure EF Core scaffold for Domain Entities and DbContext. Do not manually modify scaffolded Entities or DbContext files during coding. Database schema dictates the models.

### Key Entities

- `translation_room.translation_room_participants`
- `translation_room.translation_rooms`

## Success Criteria *(mandatory)*

- Host controls work and are persisted correctly.
- Unauthorized participant-management requests fail.
- Tests cover host control success paths, permission failures, and reconnect/leave behavior.

## Implementation Notes

- Use the `IsMuted` field in `TranslationRoomParticipant` to store the enable/disable translation audio state.
- Create endpoints such as `PUT /api/v1/translation-rooms/{roomId}/participants/{participantId}/audio` for enabling/disabling audio.
- Create endpoint `PUT /api/v1/translation-rooms/{roomId}/participants/{participantId}/kick` for kicking.
- Create endpoint `PUT /api/v1/translation-rooms/{roomId}/participants/me/leave` for leaving the room.
- Create endpoint `GET /api/v1/translation-rooms/{roomId}/participants` for listing participants.
- "Disable translation audio" means the system will only stop relaying translated audio to the participant (they can still send audio). This is mapped to `is_muted = true` and handled by the real-time media server.
