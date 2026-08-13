-- Migration: 20260812160000_add_translation_room_active_host
-- Created At: 2026-08-12
-- Description:
--   WT-359 / WT-358 / WT-353: "who is the host" had two answers, in two services, and
--   Transfer Host only ever moved one of them.
--
--   WHAT WAS BROKEN
--     MeetingRoomService.TransferHostAsync writes meeting.meeting_rooms.active_host_id and
--     nothing else. The translation-room service never learned about it: JoinTranslationRoomAsync
--     computes `isHost` as `translationRoom.HostId == userId`, and HostId is the CREATOR, stamped
--     once at creation and never moved. TranslationRoomParticipantMapper.UpdateFrom then re-asserts
--     Role = HOST for that user on every rejoin (BR-004).
--
--     So the original host transferred the room away, left, came back — and was handed the room
--     back. 100% reproducible, no workaround. Every host-gated operation in the
--     translation-room service (start, pause, resume, stop, end, cancel, settings) compares
--     against HostId too, which is why the NEW host could not end the meeting while the OLD one
--     still could.
--
--   WHY A NEW COLUMN AND NOT AN UPDATE TO host_id
--     host_id answers "who booked this". It is what the meeting list filters by, what a recurring
--     series belongs to, and what usage is attributed to. A host handover during one meeting is
--     not a transfer of the booking, and overwriting host_id would silently move all of that too
--     — including, for an occurrence of a recurring series, the ownership of every future
--     occurrence.
--
--     active_host_id answers the narrower question "who is running it right now". NULL means
--     nobody has taken it over, so the booker is still running it — which is true of every room
--     that existed before this column, and is why no backfill is needed.
--
--       effective host = COALESCE(active_host_id, host_id)
--
--     The name deliberately matches meeting.meeting_rooms.active_host_id, which already means
--     exactly this. Two columns, one vocabulary.
--
--   NO FOREIGN KEY
--     Same as host_id: the user lives in the auth service's database. The id is carried, not
--     enforced.

ALTER TABLE translation_room.translation_rooms
    ADD COLUMN IF NOT EXISTS active_host_id UUID;

COMMENT ON COLUMN translation_room.translation_rooms.active_host_id IS
    'WT-359: who is running this meeting NOW, after any Transfer Host. NULL means nobody took it over and host_id (the booker) is still running it. Effective host = COALESCE(active_host_id, host_id). Never overwrite host_id to record a handover - that column owns the booking, not the session.';

-- Reading "the rooms this user currently hosts" has to consider both columns, and the
-- effective-host predicate is COALESCE(active_host_id, host_id) = :userId. An index on the bare
-- column serves the far more common half of that (a room that WAS transferred), and is partial
-- so it costs nothing for the overwhelming majority of rooms that never were.
CREATE INDEX IF NOT EXISTS translation_rooms_active_host_id_idx
    ON translation_room.translation_rooms (active_host_id)
    WHERE active_host_id IS NOT NULL;
