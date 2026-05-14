# Feature Specification: Real-time Translation & Transcript (Module 2)

**Feature Branch**: `feature/module-2-transcript`  
**Created**: 2026-05-14  
**Status**: Draft  
**Input**: User description: "WarpTalk Transcript Backend Implementation - Enterprise Grade with Redis Streams, gRPC, PgBouncer"

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Real-time STT Persistence (Priority: P1)

As a Host, I want the system to automatically capture and transcribe participant speech in real-time so that a reliable meeting record is generated and persisted.

**Why this priority**: Without the base transcription data being reliably saved, no other features (translation, correction, export) can function. It is the core data layer of Module 2.

**Independent Test**: Can be fully tested by simulating audio chunk ingestion into Redis, triggering the AI STT worker, and verifying that the `TranscriptService` consumes the `stt:results` stream and persists records in `transcript_segments` while correctly deducting billing credits.

**Acceptance Scenarios**:

1. **Given** an active room, **When** the AI STT worker publishes to `stt:results:{roomId}`, **Then** the background consumer persists the segment and increments `total_segments`.
2. **Given** a persisted segment, **When** the transaction commits, **Then** a gRPC call to `BillingService` deducts the appropriate credits.

---

### User Story 2 - Real-time Translation Persistence (Priority: P1)

As a Participant, I want to receive real-time translated segments in my target language and have them saved, so that I can understand speakers in different languages and review them later.

**Why this priority**: Core value proposition of WarpTalk. The translations must be accurately linked to their source segments.

**Independent Test**: Can be tested by verifying that `translate:results` stream messages are consumed and persisted to `transcript_translations` without duplicating existing translations (idempotent UPSERT).

**Acceptance Scenarios**:

1. **Given** a transcribed segment, **When** the AI Translation worker publishes to `translate:results:{roomId}`, **Then** the translation is persisted via UPSERT.

---

### User Story 3 - Segment Correction & Re-translation (Priority: P2)

As an authorized user (Host/Participant), I want to be able to correct transcript segments so that AI transcription errors can be fixed, and see the corrected translations.

**Why this priority**: AI STT is not 100% accurate. Corrections ensure enterprise-grade accuracy.

**Independent Test**: Can be tested by submitting a correction API request and observing that a message is published to `translate:requests:{roomId}` for re-translation.

**Acceptance Scenarios**:

1. **Given** a finalized transcript, **When** a user submits a correction, **Then** the segment's `is_corrected` flag is set and a `transcript_corrections` audit row is created.
2. **Given** a corrected segment, **When** the correction is saved, **Then** a request is sent to `translate:requests:{roomId}` and the new translation UPSERTs the old one with `is_retranslated = true`.

---

### User Story 4 - Glossary Management (Priority: P3)

As a Workspace Admin, I want to manage glossaries so that domain-specific terminology is correctly translated by the AI workers.

**Why this priority**: Essential for enterprise clients with specialized terminology (e.g., medical, legal).

**Independent Test**: Can be tested via REST API CRUD operations on glossaries and glossary terms.

**Acceptance Scenarios**:

1. **Given** a workspace, **When** an admin creates a glossary term, **Then** it is saved and synced for future AI translation jobs.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: System MUST ingest STT results from Redis Streams (`stt:results`) and persist them to PostgreSQL using `ON CONFLICT DO NOTHING` for idempotency.
- **FR-002**: System MUST deduct billing credits for transcribed segments using a gRPC call to `BillingService`.
- **FR-003**: System MUST process translations from Redis (`translate:results`) and persist them using an UPSERT pattern to safely handle re-translations.
- **FR-004**: System MUST support segment corrections by authorized users ONLY when the transcript status is `finalized`.
- **FR-005**: System MUST automatically trigger re-translation when a segment is corrected by publishing to `translate:requests:{roomId}`.
- **FR-006**: System MUST push notifications to room members via `NotificationService` gRPC when a transcript is finalized or corrected.
- **FR-007**: System MUST validate user permissions for corrections and exports via gRPC calls to `TranslationRoomService`.

### Edge Cases

- What happens when Redis consumer restarts? System uses `segment_id` as the primary key and performs idempotent inserts/upserts to prevent duplication.
- How does system handle cross-service gRPC failures? Retries with exponential backoff for `ConsumeCredits`. For non-critical notifications, failures are logged but don't block the transaction.
- What if the AI pipeline crashes? The transcript will remain in `recording` status. A background job or manual intervention will transition it to `failed`.

### Key Entities

- **Transcript**: Represents the entire meeting record. Links to `TranslationRoom`.
- **TranscriptSegment**: A single spoken utterance (STT output).
- **TranscriptTranslation**: The translated text for a specific segment.
- **TranscriptCorrection**: Audit log of user modifications to segments.
- **Glossary** & **GlossaryTerm**: Workspace-scoped dictionaries for custom translation terminology.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: The Redis background consumer can process 500+ segments/second without bottlenecking the database (using PgBouncer connection pooling).
- **SC-002**: Re-translations are processed and updated in the DB within 2 seconds of a user submitting a correction.
- **SC-003**: 100% of persisted segments have their corresponding billing credits correctly deducted.
- **SC-004**: Zero duplicate segments are created during Redis consumer pod restarts.

## Assumptions

- Gateway handles all SignalR broadcasting; TranscriptService is purely responsible for persistence and REST/gRPC API.
- PgBouncer is correctly configured in the infrastructure and accessible on port 6432.
- The Python AI Workers conform strictly to the Redis Stream field schemas expected by the backend.
