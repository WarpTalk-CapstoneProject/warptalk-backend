# Feature Specification: 5.1 Design MeetingService schema and lifecycle

**Feature Branch**: `docs/wt-109-meetingservice-schema-lifecycle`
**Created**: 2026-05-20
**Status**: approved
**Input**: Linear Ticket WT-109

## Description

Define the MeetingService business schema ownership and lifecycle for native WarpTalk meetings.

This spec documents how MeetingService persists durable business state in PostgreSQL while LiveKit remains the source of truth for high-frequency media runtime state.

## User Scenarios and Testing

**User Story**: As the backend team, we need a clear MeetingService domain model and lifecycle so implementation can proceed without duplicated or conflicting state ownership.

**Acceptance Scenarios**:

1. Given a Translation Room, when an authorized user joins, then MeetingService resolves or creates a `meeting_room` record and returns a LiveKit token.
2. Given participant and track webhook events from LiveKit, when MeetingService consumes them, then participant and track lifecycle timestamps are updated consistently.
3. Given room finish webhook, when MeetingService processes it, then room status becomes `Finished` and `ended_at` is recorded.
4. Given runtime media state (packet-level, jitter, frequent mute toggles), when designing persistence, then high-frequency state is not duplicated as core transactional state in PostgreSQL.

## Business Ownership Boundary

### MeetingService owns (durable business state)
- Meeting room identity mapped to Translation Room (`meeting.meeting_rooms`).
- Participant membership mapping (`meeting.meeting_participants`).
- Track identity and business-relevant publish/mute snapshots (`meeting.meeting_tracks`).
- Lifecycle status transitions that matter for business APIs (`Created`, `Active`, `Finished`).

### LiveKit owns (runtime state authority)
- Real-time media transport, SFU session topology, and packet-level stream state.
- Real-time participant connectivity quality and transport-level metrics.
- Instant media publish/unpublish signaling frequency.

### Integration rule
- MeetingService stores only business-relevant lifecycle snapshots from LiveKit events.
- No second authoritative runtime state machine is created in MeetingService for media internals.

## Lifecycle Model

### Meeting Room lifecycle
- `Created`: Meeting room metadata exists and can accept participants.
- `Active`: Room has active media session activity (derived from business workflow/webhooks when adopted by implementation).
- `Finished`: Room finished event received and `ended_at` stored.

### Participant lifecycle markers
- `joined_at` set when `participant_joined` webhook is received.
- `left_at` set when `participant_left` webhook is received.
- Participant identity links WarpTalk user identity to provider identity.

### Track lifecycle markers
- `published_at` set on first `track_published`.
- `unpublished_at` updated on `track_unpublished`.
- `is_muted` reflects latest business-relevant mute snapshot.

## Data Model References

Primary schema notes are documented in:
- `schema-notes.md`
- `lifecycle-map.md`

## Constraints and Guardrails

- `translation_room_id` is the stable business linkage key from MeetingService to TranslationRoomService.
- Provider identifiers (`provider_room_name`, `provider_identity`, `provider_track_id`) are integration identifiers and must remain stable once issued.
- Cascade delete remains room -> participants -> tracks for cleanup consistency.
- Avoid writing high-frequency telemetry rows into core MeetingService tables.

## Verification Plan

- Confirm entity and mapping alignment with `MeetingDbContext`.
- Confirm service flow alignment with `MeetingRoomService` and `MeetingWebhookService`.
- Build MeetingService solution to validate docs are aligned with compile-ready source tree.
