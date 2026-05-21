# Feature Specification: 1.6 Build Audio Routing Context

**Feature Branch**: `feature/wt-66-16-build-audio-routing-context`
**Created**: 2026-05-16
**Status**: approved
**Input**: Linear Ticket WT-66

## Description

Implement audio route data used by real-time translation and voice output.

## User Scenarios & Testing

**User Story**: As the realtime translation pipeline, I need deterministic audio route context so that each participant receives the correct translated stream.

**Acceptance Scenarios**:

1. **Given** participants with different listen languages, **When** route generation runs, **Then** the system creates source-to-target routes with correct source and target language metadata.
2. **Given** a participant is both source and target, **When** route generation evaluates that pair, **Then** no self-route is created.
3. **Given** voice cloning is enabled for an allowed route, **When** the route is persisted, **Then** `voice_clone_enabled` is available to downstream realtime services.

## Business Rules *(Xác nhận rule nghiệp vụ)*

### 1. Route Generation
- System MUST generate source-to-target participant routes based on room participants and language policy.
- Source and target participant cannot be the same (no self-route is created).
- Route respects participant speak/listen language.
- **Diff and Upsert Logic (Continuity Rule)**: 
  - The generation process MUST use an upsert strategy rather than a clear-and-recreate approach.
  - **Stable Logical Key**: A route is uniquely identified by `(RoomId, SourceParticipantId, TargetParticipantId, SourceLanguage, TargetLanguage)`.
  - **Unchanged routes** that still match the active room policy are kept intact (`ACTIVE`) without modifying their underlying identity/StreamId. This ensures the AI worker does not drop or cut off ongoing streams across recomputes.
  - **New combinations** are created as new routes.
  - **Obsolete routes** (e.g., participants left, or changed languages) are explicitly marked as `INACTIVE` rather than hard-deleted.

### 2. Route Context
- System MUST persist route context including source participant, target participant, source language, target language, status, and stream ID.
- Voice clone flag (`voice_clone_enabled`) is available per route to downstream realtime services.

### 3. Route Lifecycle (Status Management)
- **Backend / Route Service**: Acts as the sole owner of `audio_route.status`.
- **Realtime Pipeline**: Acts as the consumer and applies runtime guards based on this status.
- **`PENDING`**: 
  - Room is in `SCHEDULED` or `WAITING` state.
  - Participant has not joined yet.
  - Streams or runtime context are not fully available yet.
- **`ACTIVE`**:
  - Room is running realtime (`IN_PROGRESS`).
  - Participant is ready/connected.
  - Route is valid, complete, and ready to be used by the realtime pipeline.
- **`INACTIVE`**:
  - Room is closed/ended (terminal status such as `ENDED`, `CANCELLED`, `FAILED`).
  - Participant left the room.
  - Route is replaced by a new configuration or becomes invalid.

## Flow Integration *(Yêu cầu tích hợp luồng)*

### A. Realtime Pipeline Integration
- **Current state**: Gateway's `TranslationRoomHub` stores a simple Redis Hash mapping `UserId -> ListenLanguage` upon `JoinTranslationRoom`, and forwards audio chunks to Redis Streams. The AI worker translates based solely on this hash map.
- **New Flow**: System MUST expose route context to realtime translation and voice output services via two specific APIs:
  - `POST /api/v1/translation-rooms/{roomId}/routes/generate`: Internal API used to recompute and persist routing logic whenever the room changes (e.g., participants join/leave, settings change).
  - `GET /api/v1/translation-rooms/{roomId}/routes`: API used for frontend or fallback reads. This is **NOT** the main path for the AI Worker.
- **Stream Distribution Rule**: The system MUST guarantee that the translated stream generated for a specific route is delivered *exclusively* to the exact `TargetParticipantId` defined in that route. Broadcasting a translated stream globally to the entire room is strictly prohibited.
- **Event-Driven Redis Integration**: 
  - `POST /generate` MUST perform route generation, persist to DB, and write to Redis as atomically as possible.
  - **Redis Cache Key**: `translationRoom:{roomId}:audio_routes`.
  - **Redis Cache Value Structure**: A JSON object containing `routes` (array of routes), `version` (int/guid), `generated_at` (timestamp), and `room_status`.
  - **Pub/Sub Payload**: Upon update, the Backend MUST publish an event to Redis Pub/Sub. The event payload MUST package the full JSON of the new routes. This allows the AI Worker to apply the routes immediately without an extra round-trip to Redis or HTTP.
  - The AI Worker strictly consumes only routes with `status = active` (or `pending` awaiting bind) from this payload/cache.
- **Separation of Concerns (Generation vs. Consumption)**:
  - **Route Generation Layer (Route Service)**: Applies core room policies. It computes and persists the routes as the absolute source of truth and is the sole owner of `audio_route.status`.
  - **Realtime Pipeline**: Must consume precomputed audio routes as the source of truth from Redis. It strictly processes only valid, provided routes.
  - **Pipeline Reporting (Event-Driven via Redis Streams)**: 
    - The AI Worker MUST NOT update the database directly.
    - When the worker needs to report a runtime update (e.g., stream bound successfully with a `StreamId`, or stream failed), it publishes an event to a Redis Stream: `translationRoom:{roomId}:system_events`.
    - **Event Payload Schema**: MUST include `route_id`, `stream_id`, `event_type`, `timestamp`, and `version/idempotency_key`.
    - **Backend Consumer**: The Backend runs a consumer group on this stream to authoritatively update the DB and refresh the Redis cache.
    - **HTTP Fallback**: `PATCH /api/v1/translation-rooms/{roomId}/audio-routes/{routeId}/runtime` is maintained strictly as an administrative or fallback path, NOT the main hot path.
  - **Critical Guards for Backend Consumer**:
    - Processing MUST be idempotent (using the `idempotency_key` or `version`).
    - **Race Condition Guard**: If the Backend receives an event for a route that is already marked `INACTIVE` (e.g., participant already left), the Backend MUST reject/ignore the event to prevent reviving obsolete routes.
    - Redis Streams is solely the event transport layer; the PostgreSQL Database remains the ultimate source of truth.

### B. Voice_clone_enabled Update Handling
- **Client Action**: When a participant toggles the Voice Clone feature (even if the participant is in `WAITING` status or the room is in `WAITING`/`IN_PROGRESS` state), the client makes an HTTP PATCH request to the Backend. This allows users to configure their voice cloning preferences before the session fully begins.
- **Backend Responsibility**: 
  - The Backend is the source of truth. It updates the database setting `voice_clone_enabled = true/false` for the associated routes.
  - The Backend instantly updates the JSON cache `translationRoom:{roomId}:audio_routes`.
  - The Backend fires an `AUDIO_ROUTES_UPDATED` event to the Redis Pub/Sub channel `translationRoom:{roomId}:events` containing the updated payload.
  - The Backend MAY also send a SignalR/WebSocket message via the Gateway strictly to notify other clients/UIs (e.g., to show a glowing Voice Clone icon next to the user's avatar).
- **AI Worker Responsibility**:
  - The AI Worker MUST NOT connect to the Gateway for this configuration.
  - Because the AI Worker is already subscribed to the Redis Pub/Sub channel, it instantly receives the updated route configuration payload.
  - The Worker reads the new `voice_clone_enabled` flag and dynamically applies or removes the voice cloning model to the ongoing audio stream without dropping the connection (Stream Continuity).

## Data Model *(Rà schema/data model)*

### TranslationRoomAudioRoute (`translation_room.translation_room_audio_routes`)
- Stores source participant, target participant, source language, target language.
- `voice_clone_enabled` flag per route.
- Route status and stream ID.

### Key Entities
- `translation_room.translation_room_audio_routes`
- `translation_room.translation_room_participants`
- `translation_room.translation_rooms`

## Validation Logic *(Thiết kế validation rule)*

- **Prevent invalid routes**: System MUST prevent invalid routes such as source participant equal to target participant.
- Route generation supports one-to-one and multi-language rooms.

## Event-Driven State Machine Architecture

To handle the complexity of realtime audio routing (including degraded states and recoveries) without modifying the database persistence layer, the system implements an **Event-driven State Machine with Single Writer Authority** at the **Route Level**.

### 1. State Orchestrator (Single Authority)
- **Backend (`TranslationRoomService`)** is the sole authoritative state machine.
- It is the only component allowed to compute state transitions and persist `audio_route.status`.
- It processes events sequentially per `room_id` / `route_id` to prevent split-brain and race conditions.

### 2. Runtime Pipeline (AI Worker)
- The AI Worker strictly consumes the Redis routing snapshot.
- It **never** writes to the PostgreSQL Database directly.
- Upon encountering a runtime signal (e.g., latency, disconnect), it wraps the signal into a standard event envelope and publishes it to Redis Streams.

### 3. State Machine Engine (13-State Deterministic Model)
The routing context follows a strict 13-state deterministic engine applied to individual routes:
- **Allowed States**: `IDLE`, `ROUTING_READY`, `AUDIO_ROUTING_ACTIVE`, `AUDIO_ROUTING_PAUSED`, `STT_DEGRADED`, `TRANSLATION_DEGRADED`, `TTS_DEGRADED`, `VOICE_CLONE_FALLBACK`, `TEXT_ONLY_MODE`, `STOPPING`, `FINALIZING_ARTIFACTS`, `FINALIZING_ARTIFACTS_FAILED`, `COMPLETED`.
- **Telemetry Priority Resolution**: A background telemetry sweep evaluates granular volatile latency and availability flags stored in Redis (e.g. `stt_degraded`, `translation_degraded`, `tts_degraded`, `voice_clone_status`, `delivery_mode`). It uses `AudioRoutePriorityResolver` to compute the effective status on a strict priority scale:
  `TEXT_ONLY_MODE` > `VOICE_CLONE_FALLBACK` > `TTS_DEGRADED` > `TRANSLATION_DEGRADED` > `STT_DEGRADED` > `AUDIO_ROUTING_ACTIVE`.
  A single event `telemetry_state_updated` transitions PostgreSQL and Redis cache atomically.
- **Update Protection Rule**: During `AUDIO_ROUTING_PAUSED` state, background telemetry updates are **prevented** from modifying the PostgreSQL canonical state. When `room_resume` is triggered, the database state resets to `AUDIO_ROUTING_ACTIVE`, allowing subsequent telemetry updates to evaluate the state.
- **Locks & Sweeper Recovery**: `COMPLETED` is a terminal sink state. `FINALIZING_ARTIFACTS_FAILED` routes are automatically scanned by a recovery sweeper to retry or trigger `finalization_abandoned` if recovery limits (dynamic sweeps) are exhausted.
- **Immediate RAM Cache Cleanup**: Upon entering `COMPLETED` or aborting via `finalization_abandoned`, the system executes **Unified Redis Cache Cleanup**, deleting both telemetry and transcript keys immediately from memory, avoiding any TTL leakage.

### 4. Data Model Strategy
- **`translation_room_audio_routes.status`**: Repurposed to store the 13 state machine statuses.
- **Optimistic Concurrency**: Managed via `updated_at` timestamps or guaranteed sequential event execution.
- **Redis Snapshot**: A hot-read JSON cache containing `status`, `version` (timestamp-based), `updated_at`, and the array of `routes`.

## Implementation Plan

### 1. Entity and Repository
- [ ] Define/update `TranslationRoomAudioRoute` entity and schema.
- [ ] Implement repository operations for route generation and retrieval.

### 2. Service Integration
- [ ] Implement service method to generate source-to-target participant routes.
- [ ] Provide API/service method for realtime pipeline to fetch route context.

## Verification Plan

### Automated Tests
- Tests cover route generation for one-to-one and multi-language rooms.
- Tests cover invalid route prevention.
- Tests cover voice clone metadata.
- **Independent Test**: Can be tested independently by creating a room with participants and language preferences, generating routes, and verifying source-target-language mappings.
