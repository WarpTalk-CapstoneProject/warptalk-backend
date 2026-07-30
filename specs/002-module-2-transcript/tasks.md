---
description: "Task list for Real-time Translation & Transcript (Module 2) implementation"
---

# Tasks: Real-time Translation & Transcript (Module 2)

**Input**: Design documents from `/specs/002-module-2-transcript/`
**Prerequisites**: plan.md (required), spec.md (required for user stories), research.md, data-model.md, contracts/

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (e.g., US1, US2, US3)
- Include exact file paths in descriptions

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Project initialization and basic structure

- [x] T001 Update `translation_room.proto` to add `GetParticipantsByRoomId` in `warptalk-backend/shared/WarpTalk.Shared/Protos/translation_room.proto`
- [x] T002 Add required NuGet packages to `WarpTalk.TranscriptService.Infrastructure` (EF Core 10, StackExchange.Redis)

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Core infrastructure that MUST be complete before ANY user story can be implemented

**⚠️ CRITICAL**: No user story work can begin until this phase is complete

- [x] T003 Setup `TranscriptDbContext` with basic schema configurations in `WarpTalk.TranscriptService.Infrastructure/Persistence/TranscriptDbContext.cs`
- [x] T004 Implement `TranscriptStatus` constants mapping to DB Enum in `WarpTalk.TranscriptService.Domain/Constants/TranscriptStatus.cs`
- [x] T005 Configure Redis `IConnectionMultiplexer` in `WarpTalk.TranscriptService.Infrastructure/DependencyInjection.cs`
- [x] T006 Implement base abstract Redis Consumer loop logic in `WarpTalk.TranscriptService.Infrastructure/Redis/BaseRedisConsumer.cs`

**Checkpoint**: Foundation ready - user story implementation can now begin in parallel

---

## Phase 3: User Story 1 - Real-time STT Persistence (Priority: P1) 🎯 MVP

**Goal**: Automatically capture and transcribe participant speech in real-time so that a reliable meeting record is generated and persisted.
**Independent Test**: Simulate audio chunk ingestion into Redis, triggering the AI STT worker, and verify that `TranscriptService` consumes `stt:results` and persists.

### Implementation for User Story 1

- [x] T007 [P] [US1] Create `Transcript` model in `WarpTalk.TranscriptService.Domain/Entities/Transcript.cs`
- [x] T008 [P] [US1] Create `TranscriptSegment` model in `WarpTalk.TranscriptService.Domain/Entities/TranscriptSegment.cs`
- [x] T009 [US1] Implement `TranscriptSegmentRepository` with UPSERT logic (`ON CONFLICT`) in `WarpTalk.TranscriptService.Infrastructure/Persistence/Repositories/TranscriptSegmentRepository.cs`
- [x] T010 [US1] Implement `SttResultConsumerService` inheriting `BaseRedisConsumer` to consume `stt:results:{roomId}` in `WarpTalk.TranscriptService.Infrastructure/Redis/SttResultConsumerService.cs`
- [x] T011 [US1] Integrate `BillingGrpcClient` call inside consumer for credit deduction in `WarpTalk.TranscriptService.Application/Services/BillingGrpcClient.cs`

**Checkpoint**: At this point, STT ingestion and persistence should be fully functional and idempotent.

---

## Phase 4: User Story 2 - Real-time Translation Persistence (Priority: P1)

**Goal**: Automatically translate transcribed text into multiple selected languages and persist them.
**Independent Test**: Publish test payload to `translate:results:{roomId}` stream and verify database upsert.

### Implementation for User Story 2

- [ ] T012 [P] [US2] Create `TranscriptTranslation` model in `WarpTalk.TranscriptService.Domain/Entities/TranscriptTranslation.cs`
- [ ] T013 [US2] Implement UPSERT logic for translations in `WarpTalk.TranscriptService.Infrastructure/Persistence/Repositories/TranscriptTranslationRepository.cs`
- [ ] T014 [US2] Implement `TranslationResultConsumerService` consuming `translate:results:{roomId}` in `WarpTalk.TranscriptService.Infrastructure/Redis/TranslationResultConsumerService.cs`

**Checkpoint**: At this point, both base transcriptions and translated chunks are persisted concurrently from Redis streams.

---

## Phase 5: User Story 3 - Participant Correction & AI Alignment (Priority: P2)

**Goal**: Allow meeting participants to correct inaccuracies in the transcript/translation manually.
**Independent Test**: Call REST API `/api/v1/transcripts/{id}/segments/{segmentId}/correct` and verify the segment is updated.

### Implementation for User Story 3

- [ ] T015 [P] [US3] Create `TranscriptCorrection` model in `WarpTalk.TranscriptService.Domain/Entities/TranscriptCorrection.cs`
- [ ] T016 [US3] Implement `ParticipantGrpcClient` to validate user role in room via `GetParticipantsByRoomId` in `WarpTalk.TranscriptService.Application/Services/ParticipantGrpcClient.cs`
- [ ] T017 [US3] Implement `CorrectionService` to handle domain logic in `WarpTalk.TranscriptService.Application/Services/CorrectionService.cs`
- [ ] T018 [US3] Create REST endpoint `POST /api/v1/transcripts/{transcriptId}/segments/{segmentId}/correct` in `WarpTalk.TranscriptService.API/Controllers/CorrectionController.cs`

**Checkpoint**: Users can now manually correct transcripts.

---

## Phase 6: User Story 4 - Glossary Management (Priority: P2)

**Goal**: Maintain consistent translations using context-specific vocabulary mappings.
**Independent Test**: CRUD operations on Glossary endpoints.

### Implementation for User Story 4

- [ ] T019 [P] [US4] Create `Glossary` and `GlossaryTerm` models in `WarpTalk.TranscriptService.Domain/Entities/Glossary.cs`
- [ ] T020 [US4] Implement CRUD logic in `GlossaryService` in `WarpTalk.TranscriptService.Application/Services/GlossaryService.cs`
- [ ] T021 [US4] Create REST endpoints in `WarpTalk.TranscriptService.API/Controllers/GlossaryController.cs`

---

## Phase N: Polish & Cross-Cutting Concerns

**Purpose**: Improvements that affect multiple user stories

- [ ] T022 Update EF Core Migrations: `dotnet ef migrations add InitTranscriptService -p WarpTalk.TranscriptService.Infrastructure -s WarpTalk.TranscriptService.API`
- [ ] T023 Setup `Serilog` specific logging for Redis Consumer errors/replays
- [ ] T024 Write documentation on how to trigger local SignalR via Gateway while testing Transcript Persistence.

---

## Dependencies & Execution Order

### User Story Dependencies

- **User Story 1 (P1)**: Can start after Foundational (Phase 2)
- **User Story 2 (P1)**: Can start after Foundational (Phase 2). Independent from US1 implementation (different Redis stream and DB table), but relies on segments existing in the DB for Foreign Keys.
- **User Story 3 (P2)**: Depends on US1 (segment must exist to be corrected).
- **User Story 4 (P2)**: Independent of other stories, can be developed at any time.

### Implementation Strategy

#### MVP First (User Story 1 Only)

1. Complete Phase 1: Setup
2. Complete Phase 2: Foundational (CRITICAL)
3. Complete Phase 3: User Story 1 (STT Persistence)
4. **STOP and VALIDATE**: Test Redis Consumer ingestion and PostgreSQL UPSERT logic.
5. Merge MVP.
