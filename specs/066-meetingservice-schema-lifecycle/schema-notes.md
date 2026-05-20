# Schema Notes: meeting schema (WT-109)

## Tables

### `meeting.meeting_rooms`
- `id` UUID PK
- `translation_room_id` UUID (business link)
- `provider_room_name` VARCHAR(255) (provider link)
- `status` ENUM-like string (`Created`, `Active`, `Finished`)
- `ended_at` TIMESTAMP NULL
- audit columns (`is_active`, `created_at`, `updated_at`, soft-delete fields)

### `meeting.meeting_participants`
- `id` UUID PK
- `meeting_room_id` UUID FK -> `meeting_rooms.id`
- `user_id` UUID NULL (WarpTalk user link)
- `provider_identity` VARCHAR(255)
- `joined_at` TIMESTAMP NULL
- `left_at` TIMESTAMP NULL
- audit columns

### `meeting.meeting_tracks`
- `id` UUID PK
- `meeting_participant_id` UUID FK -> `meeting_participants.id`
- `provider_track_id` VARCHAR(255)
- `media_type` string enum (`Audio` or `Video`)
- `is_muted` BOOLEAN
- `published_at` TIMESTAMP NULL
- `unpublished_at` TIMESTAMP NULL
- audit columns

## Indexes (existing)

- `idx_meeting_rooms_translation_room_id`
- `idx_meeting_participants_meeting_room_id`
- `idx_meeting_participants_user_id`
- `idx_meeting_tracks_meeting_participant_id`

## Relationship Rules

- One meeting room has many participants.
- One participant has many tracks.
- Delete behavior is cascade down the ownership chain.

## Recommended follow-up for implementation tickets

- Add unique constraint for `(meeting_room_id, user_id)` when `user_id` is present to prevent duplicate participant business records.
- Add unique constraint for `provider_track_id` when provider guarantees global uniqueness.
- Add idempotency-safe upsert pattern in webhook handlers for repeated deliveries.
