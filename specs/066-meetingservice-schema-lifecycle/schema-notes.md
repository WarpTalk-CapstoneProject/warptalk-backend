# Data Model: MeetingService

Defines the durable business schema for native WarpTalk meeting sessions.
LiveKit remains the runtime authority for high-frequency media state; this
schema stores business identifiers, lifecycle checkpoints, and audit-friendly
snapshots needed by WarpTalk APIs and downstream services.

## 1. MeetingRoom

Maps one WarpTalk Translation Room to one provider-backed meeting room.

| Field | Type | Modifiers | Description |
|-------|------|-----------|-------------|
| `id` | UUID | PK, default `uuid_generate_v7()` | Unique MeetingService room ID |
| `translation_room_id` | UUID | Index, NOT NULL | External TranslationRoomService room ID. Business link, no cross-service FK |
| `provider_room_name` | VARCHAR(255) | NOT NULL | Stable LiveKit/provider room name |
| `status` | ENUM-like string | NOT NULL, default `Created` | Durable business lifecycle: `Created`, `Active`, `Finished` |
| `ended_at` | TIMESTAMP | NULL | Set when provider/business finish event is processed |
| `is_active` | BOOLEAN | NOT NULL, default true | Soft-active flag |
| `created_at` | TIMESTAMP | NOT NULL, default `now()` | UTC creation timestamp |
| `created_by` | UUID | NULL | External AuthService user ID |
| `updated_at` | TIMESTAMP | NOT NULL, default `now()` | UTC update timestamp |
| `updated_by` | UUID | NULL | External AuthService user ID |
| `deleted_at` | TIMESTAMP | NULL | Soft-delete timestamp |
| `deleted_by` | UUID | NULL | External AuthService user ID |

## 2. MeetingParticipant

Stores participant membership and provider identity snapshots for a meeting.

| Field | Type | Modifiers | Description |
|-------|------|-----------|-------------|
| `id` | UUID | PK, default `uuid_generate_v7()` | Unique meeting participant ID |
| `meeting_room_id` | UUID | FK, Index, NOT NULL | Parent `meeting.meeting_rooms.id` |
| `user_id` | UUID | Index, NULL | External AuthService user ID. Nullable for guest/provider-only participants |
| `provider_identity` | VARCHAR(255) | NOT NULL | Stable LiveKit/provider participant identity |
| `joined_at` | TIMESTAMP | NULL | Set from provider participant joined event |
| `left_at` | TIMESTAMP | NULL | Set from provider participant left event |
| `is_active` | BOOLEAN | NOT NULL, default true | Soft-active flag |
| `created_at` | TIMESTAMP | NOT NULL, default `now()` | UTC creation timestamp |
| `created_by` | UUID | NULL | External AuthService user ID |
| `updated_at` | TIMESTAMP | NOT NULL, default `now()` | UTC update timestamp |
| `updated_by` | UUID | NULL | External AuthService user ID |
| `deleted_at` | TIMESTAMP | NULL | Soft-delete timestamp |
| `deleted_by` | UUID | NULL | External AuthService user ID |

## 3. MeetingTrack

Stores provider track identity and business-relevant media lifecycle snapshots.

| Field | Type | Modifiers | Description |
|-------|------|-----------|-------------|
| `id` | UUID | PK, default `uuid_generate_v7()` | Unique meeting track ID |
| `meeting_participant_id` | UUID | FK, Index, NOT NULL | Parent `meeting.meeting_participants.id` |
| `provider_track_id` | VARCHAR(255) | NOT NULL | Stable LiveKit/provider track ID |
| `media_type` | ENUM-like string | NOT NULL | `Audio` or `Video` |
| `is_muted` | BOOLEAN | NOT NULL, default false | Latest business-relevant mute snapshot, not packet-level runtime truth |
| `published_at` | TIMESTAMP | NULL | Set from first provider track published event |
| `unpublished_at` | TIMESTAMP | NULL | Set from provider track unpublished event |
| `is_active` | BOOLEAN | NOT NULL, default true | Soft-active flag |
| `created_at` | TIMESTAMP | NOT NULL, default `now()` | UTC creation timestamp |
| `created_by` | UUID | NULL | External AuthService user ID |
| `updated_at` | TIMESTAMP | NOT NULL, default `now()` | UTC update timestamp |
| `updated_by` | UUID | NULL | External AuthService user ID |
| `deleted_at` | TIMESTAMP | NULL | Soft-delete timestamp |
| `deleted_by` | UUID | NULL | External AuthService user ID |

## 4. Indexes

| Index | Table | Columns | Purpose |
|-------|-------|---------|---------|
| `idx_meeting_rooms_translation_room_id` | `meeting.meeting_rooms` | `translation_room_id` | Resolve meeting room by TranslationRoomService ID |
| `idx_meeting_participants_meeting_room_id` | `meeting.meeting_participants` | `meeting_room_id` | List participants for a meeting room |
| `idx_meeting_participants_user_id` | `meeting.meeting_participants` | `user_id` | Query meetings for a WarpTalk user |
| `idx_meeting_tracks_meeting_participant_id` | `meeting.meeting_tracks` | `meeting_participant_id` | List tracks for a participant |

## 5. Relationship Rules

- One `MeetingRoom` has many `MeetingParticipant` records.
- One `MeetingParticipant` has many `MeetingTrack` records.
- Delete behavior cascades from `meeting_rooms` to `meeting_participants` to `meeting_tracks`.
- Cross-service identifiers such as `translation_room_id` and `user_id` are business references only and do not create physical foreign keys across service boundaries.

## 6. Runtime Ownership Rules

- LiveKit owns realtime media transport, packet-level stream state, network quality, and high-frequency participant connectivity.
- MeetingService owns durable business lifecycle snapshots needed for APIs, audit, downstream transcript triggering, and historical lookup.
- MeetingService MUST NOT persist high-frequency telemetry rows in core meeting tables.
- `is_muted` in `meeting_tracks` is a latest-known business snapshot. LiveKit remains the source of truth for instantaneous media runtime state.

## 7. Recommended Follow-up Constraints

- Add a unique constraint for `(meeting_room_id, user_id)` when `user_id` is present to prevent duplicate registered participant records.
- Add a unique constraint for `provider_track_id` if the provider guarantees global track ID uniqueness.
- Add idempotency-safe upsert behavior in webhook handlers for repeated provider deliveries.
