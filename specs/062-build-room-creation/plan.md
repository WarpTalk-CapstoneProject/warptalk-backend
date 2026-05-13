# Implementation Plan: 1.1 Build Room Creation and Scheduling Flow

## Phase -1: Constitution Gates Verification

- [x] **Article I (Clean Architecture)**: Domain, Application, Infrastructure, and API layers will be strictly separated. Domain will contain entities and enums. Application will contain CQRS commands and validators. Infrastructure will handle DbContext. API will expose the controller.
- [x] **Article II (Communication)**: This feature primarily deals with internal DB state. Validation of `workspace_id` and `host_id` will rely on JWT claims extracted at the API layer, minimizing synchronous inter-service calls unless absolutely required.
- [x] **Article III (Modern Standards)**: All new IDs will use UUID v7. No hardcoded magic strings (using `Constants` and `Enums`).
- [x] **Article IV (TDD)**: Unit tests for validators and handlers, and Integration tests using Testcontainers will be written *before* implementation.
- [x] **Article VI (API Standards)**: The new endpoint will be `POST /api/v1/translation-rooms` and will return RFC 7807 `ProblemDetails` on validation failure.

## Proposed Changes

---

### 1. Domain Layer (`WarpTalk.TranslationRoomService.Domain`)

#### [NEW] `Enums/RoomStatus.cs`
- Define `RoomStatus` enum with values: `Scheduled`, `Waiting`, `InProgress`, `Paused`, `Ended`, `Cancelled`, `Failed`, `Expired`.

#### [NEW] `Enums/TranslationRoomType.cs`
- Define `TranslationRoomType` enum with values: `Group`, `P2P`.

#### [NEW] `Entities/TranslationRoom.cs`
- Properties: `Id` (Guid), `WorkspaceId` (Guid), `HostId` (Guid), `Title` (string), `Description` (string), `TranslationRoomCode` (string, max 12), `Status` (RoomStatus), `TranslationRoomType` (TranslationRoomType), `MaxParticipants` (int), `SourceLanguage` (string), `TargetLanguages` (string/JSON array), `ScheduledAt` (DateTimeOffset?), `CreatedAt`, `UpdatedAt`.

#### [NEW] `Constants/TranslationRoomConstants.cs`
- Constants for validation (e.g., max title length, room code length).

#### [NEW] `Interfaces/ITranslationRoomRepository.cs`
- Define `AddAsync`, `ExistsByCodeAsync`.

---

### 2. Application Layer (`WarpTalk.TranslationRoomService.Application`)

#### [NEW] `DTOs/Requests/CreateTranslationRoomRequest.cs`
- Fields: `WorkspaceId`, `Title`, `Description`, `TranslationRoomType`, `MaxParticipants`, `SourceLanguage`, `TargetLanguages`, `ScheduledAt`.

#### [NEW] `DTOs/Responses/TranslationRoomResponse.cs`
- Fields: `Id`, `TranslationRoomCode`, `Status`, `ScheduledAt`, etc.

#### [NEW] `Commands/CreateTranslationRoom/CreateTranslationRoomCommand.cs`
- MediatR Command containing the request and `HostId`.

#### [NEW] `Commands/CreateTranslationRoom/CreateTranslationRoomCommandValidator.cs`
- FluentValidation rules:
  - `Title` must not be empty.
  - `ScheduledAt` MUST be strictly greater than `DateTimeOffset.UtcNow` (if provided).
  - Note: Limits on `MaxParticipants` and max allowed future `ScheduledAt` are deferred to Technical Debt.

#### [NEW] `Commands/CreateTranslationRoom/CreateTranslationRoomCommandHandler.cs`
- Logic:
  1. Determine initial status: If `ScheduledAt` has a value -> `RoomStatus.Scheduled`. Else -> `RoomStatus.Waiting`.
  2. Generate unique 12-char alphanumeric `TranslationRoomCode` using a Helper.
  3. Ensure code collision does not happen (retry mechanism if `ExistsByCodeAsync` is true).
  4. Create `TranslationRoom` entity.
  5. Save via `ITranslationRoomRepository`.
  6. Return mapped `TranslationRoomResponse`.

#### [NEW] `Helpers/RoomCodeGenerator.cs`
- Utility to generate 12-character alphanumeric strings.

---

### 3. Infrastructure Layer (`WarpTalk.TranslationRoomService.Infrastructure`)

#### [MODIFY] `Persistence/TranslationRoomDbContext.cs`
- Add `DbSet<TranslationRoom> TranslationRooms`.
- Configure entity mapping (schema `translation_room`, table `translation_rooms`, `TranslationRoomCode` as unique, Enums mapped as strings).

#### [NEW] `Repositories/TranslationRoomRepository.cs`
- Implement `ITranslationRoomRepository` using EF Core.

---

### 4. API Layer (`WarpTalk.TranslationRoomService.API`)

#### [NEW] `Controllers/V1/TranslationRoomsController.cs`
- `[HttpPost]` endpoint mapped to `/api/v1/translation-rooms`.
- Extracts `HostId` from the authenticated user's JWT claims.
- Sends `CreateTranslationRoomCommand` via MediatR.
- Returns `201 Created` with the room details or `400 Bad Request` (ProblemDetails) on validation errors.

---

## Verification Plan

### Automated Tests
1. **Unit Tests**:
   - `CreateTranslationRoomCommandValidatorTests`: Test `ScheduledAt > DateTimeOffset.UtcNow` logic.
   - `CreateTranslationRoomCommandHandlerTests`: Test room code generation, initial status assignment (`SCHEDULED` vs `WAITING`), and repository interaction.
   - `RoomCodeGeneratorTests`: Verify the generated string length and format.
2. **Integration Tests**:
   - `TranslationRoomsControllerTests`: Test the `POST /api/v1/translation-rooms` endpoint with valid payloads (should return 201 and persist to PostgreSQL via Testcontainers) and invalid payloads (should return 400 ProblemDetails).

### Manual Verification
- Run the API locally via Docker Compose.
- Call the endpoint using Postman to create an instant room and a scheduled room.
- Verify the DB records in `translation_room.translation_rooms`.

## User Review Required

- Is the approach of extracting `HostId` from JWT and optionally receiving `WorkspaceId` in the request body acceptable, or should `WorkspaceId` also be extracted from the JWT/Headers?
- Should the `TranslationRoomCode` be completely random (e.g., `A8F3K9X2M1QZ`) or hyphen-separated (e.g., `abc-defg-hij`)?
