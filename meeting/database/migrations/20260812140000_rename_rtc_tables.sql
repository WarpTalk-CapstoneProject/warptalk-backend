-- Migration: 20260812140000_rename_rtc_tables
-- Created At: 2026-08-12
-- Description:
--   Two tables in the `meeting` schema say something they are not.
--
--   THE SCHEMA IS NOT THE MEETING
--     The meeting is translation_room.translation_rooms. This schema holds the live LiveKit
--     session underneath it: who is streaming, whether the room is locked, the recording egress
--     id, host handover. Anyone reading `meeting.meeting_participants` reasonably assumes it is
--     the list of people in a meeting; it is the list of people with an RTC stream.
--
--       meeting_participants -> rtc_stream_participants
--
--   AND meeting_invitations WAS NEVER AN INVITATION
--     Its only writes are in MeetingRoomService.RejectParticipantAsync and KickParticipantAsync,
--     both setting Status = 'REVOKED', with the code's own comment reading "Create a revoked
--     invitation to prevent future joins". It is a deny list for the live session. Production
--     bears this out: one row, against 217 in translation_room.translation_room_invitations,
--     which is the real invitation table.
--
--       meeting_invitations -> rtc_session_revocations
--
--   WHY ONLY THE TABLES MOVE
--     The C# entities keep their names and are remapped with ToTable(). Renaming MeetingParticipant
--     and MeetingInvitation would ripple through MeetingRoomService, the repositories, the DTOs and
--     the controllers — a much larger diff, for a naming problem that is only visible in the
--     database and the ERD. The mapping is the seam; this is what it is for.
--
--   RENAME, NOT RECREATE
--     ALTER TABLE ... RENAME keeps the data, the primary key, the foreign keys and the indexes,
--     including their existing names. The EF configuration still references those constraint names
--     (meeting_participants_pkey and so on) and continues to match, because Postgres does not
--     rename constraints when a table is renamed. Deliberate: renaming them too would be a second
--     change with no reader.

ALTER TABLE IF EXISTS meeting.meeting_participants
    RENAME TO rtc_stream_participants;

ALTER TABLE IF EXISTS meeting.meeting_invitations
    RENAME TO rtc_session_revocations;

COMMENT ON TABLE meeting.rtc_stream_participants IS
    'Live LiveKit stream participants for a session. NOT the meeting roster - that is translation_room.translation_room_participants.';

COMMENT ON TABLE meeting.rtc_session_revocations IS
    'Deny list for a live session: a row means this user was kicked or rejected and may not rejoin. Not an invitation - see translation_room.translation_room_invitations.';
