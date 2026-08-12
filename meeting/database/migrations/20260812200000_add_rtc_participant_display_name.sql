-- Migration: 20260812200000_add_rtc_participant_display_name
-- Created At: 2026-08-12
-- Description:
--   WT-356. Chat shows a GUID where a person's name belongs.
--
--   THE COLUMN NAMED AFTER A NAME NEVER HELD ONE
--     MeetingChatMapper writes the sender like this:
--
--       SenderDisplayName = participant?.ProviderIdentity ?? "Unknown User"
--
--     and MeetingRoomService.JoinMeetingAsync sets `providerIdentity = userId.ToString()`. So
--     `meeting_chat_messages.sender_display_name` has always held a user id. Production bears
--     it out — every provider_identity in rtc_stream_participants is a bare uuid.
--
--     The frontend hid it for the common case: chatSenderName looks the sender up in the live
--     participant list first and only falls back to sender_display_name. That fallback exists so
--     somebody who has LEFT the room keeps their name on the messages they wrote — and it is
--     exactly then, when the lookup misses, that the raw uuid surfaced. The comment above it
--     reads "The server already said who sent this"; the server had said a uuid.
--
--   WHY THE NAME IS NOT ALREADY HERE
--     JoinMeetingAsync already resolves the real display name — from the caller's JWT, or over
--     gRPC from translation_room.translation_room_participants.display_name — and hands it to
--     LiveKit as the token's participantName. It was then dropped on the floor, because this
--     table had nowhere to put it. The tile above a speaker's video and the name beside their
--     chat message came from two different places, and only one of them was a name.
--
--   NULLABLE, AND NOT BACKFILLED
--     Existing rows genuinely do not know the name: what they hold is a user id, and the users
--     live in another service's database, so there is no join available to recover it here.
--     Writing the uuid into display_name would only move the defect one column across. Rows
--     written from this release forward carry the name; older messages keep whatever they were
--     stored with, and the frontend's participant lookup still covers anyone still in the room.
BEGIN;

ALTER TABLE meeting.rtc_stream_participants
    ADD COLUMN IF NOT EXISTS display_name VARCHAR(255);

COMMENT ON COLUMN meeting.rtc_stream_participants.display_name IS
    'Human-readable participant name resolved at join (JWT, else translation_room over gRPC). '
    'Distinct from provider_identity, which is the LiveKit identity and is the user id.';

COMMIT;
