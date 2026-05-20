# Lifecycle Map: MeetingService (WT-109)

## Join Flow (Business path)

1. Client calls `POST /api/v1/meetings/rooms/{translationRoomId}/join`.
2. MeetingService validates caller is host or active participant via TranslationRoom gRPC + cache.
3. MeetingService resolves or creates `meeting.meeting_rooms` row.
4. MeetingService resolves or creates `meeting.meeting_participants` row.
5. MeetingService generates LiveKit token and returns `providerRoomName` + `participantIdentity`.

## Webhook Flow (Provider -> Business snapshot)

1. LiveKit sends webhook event.
2. MeetingService validates webhook token signature.
3. Event handler maps provider identity to room/participant/track records.
4. MeetingService updates business lifecycle columns:
- participant join/left timestamps
- track publish/unpublish timestamps
- track mute snapshot
- room finish status and ended timestamp
5. For audio publish events, MeetingService emits event for downstream transcript worker.

## State Duplication Rule

- Keep LiveKit as runtime truth for real-time media behavior.
- Persist only business lifecycle checkpoints needed for API behavior, audit, and downstream integrations.
