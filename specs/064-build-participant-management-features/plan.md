# Implementation Plan: Participant Management Features

## Phase -1 Constitution Verification
- [x] Article I (Clean Architecture): Controllers route to Application layer services. Domain remains pure.
- [x] Article II (Communication): N/A (Standard REST).
- [x] Article III (Stack): .NET 10, UUID v7.
- [x] Article IV (TDD): Tests will be written first and must fail.
- [x] Article VI (API): `/api/v1/` endpoints, ProblemDetails for errors.

## Proposed Changes

### Domain Layer
- Ensure `TranslationRoomParticipantStatus` includes `KICKED`, `LEFT`, `DISCONNECTED`, `CONNECTED`.

### Application Layer
- **DTOs**: `ParticipantResponseDto`, `UpdateAudioRequestDto`.
- **ITranslationRoomService**:
  - `Task<List<ParticipantResponseDto>> GetParticipantsAsync(Guid roomId)`
  - `Task UpdateParticipantAudioAsync(Guid roomId, Guid participantId, bool isTranslationAudioEnabled, Guid requestedByUserId)`
  - `Task KickParticipantAsync(Guid roomId, Guid participantId, Guid requestedByUserId)`
  - `Task LeaveRoomAsync(Guid roomId, Guid requestedByUserId)`
- **TranslationRoomService**: Implement the above, ensuring authorization (requestedByUserId is host) and state transitions.

### API Layer
- **Validators**: Add FluentValidation for any incoming DTOs.
- **TranslationRoomsController**:
  - `GET /api/v1/translation-rooms/{roomId}/participants`
  - `PUT /api/v1/translation-rooms/{roomId}/participants/{participantId}/audio`
  - `PUT /api/v1/translation-rooms/{roomId}/participants/{participantId}/kick`
  - `PUT /api/v1/translation-rooms/{roomId}/participants/me/leave`

### Tests
- Write integration tests for success and 403 Forbidden scenarios.
- Write unit tests for service business logic.
