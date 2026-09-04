-- WT-603: remove Polls, Q&A, and Breakouts from Meeting Runtime.
--
-- Production preflight, run through the approved database diagnostic path:
--   SELECT 'poll_votes', count(*) FROM meeting.poll_votes
--   UNION ALL SELECT 'poll_options', count(*) FROM meeting.poll_options
--   UNION ALL SELECT 'polls', count(*) FROM meeting.polls
--   UNION ALL SELECT 'question_votes', count(*) FROM meeting.question_votes
--   UNION ALL SELECT 'questions', count(*) FROM meeting.questions
--   UNION ALL SELECT 'breakout_assignments', count(*) FROM meeting.breakout_assignments
--   UNION ALL SELECT 'breakout_sessions', count(*) FROM meeting.breakout_sessions;
-- Export any rows that require retention before approving the immutable release.
-- The logical-database migration runner wraps this file in a transaction and
-- records its checksum in public.service_schema_migrations.

ALTER INDEX IF EXISTS meeting.meeting_invitations_pkey
    RENAME TO rtc_session_revocations_pkey;
ALTER INDEX IF EXISTS meeting.idx_meeting_invitations_meeting_room_id
    RENAME TO idx_rtc_session_revocations_meeting_room_id;

ALTER INDEX IF EXISTS meeting.meeting_participants_pkey
    RENAME TO rtc_stream_participants_pkey;
ALTER INDEX IF EXISTS meeting.idx_meeting_participants_meeting_room_id
    RENAME TO idx_rtc_stream_participants_meeting_room_id;
ALTER INDEX IF EXISTS meeting.idx_meeting_participants_user_id
    RENAME TO idx_rtc_stream_participants_user_id;

ALTER INDEX IF EXISTS meeting.idx_meeting_tracks_meeting_participant_id
    RENAME TO idx_meeting_tracks_rtc_stream_participant_id;

DO $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM information_schema.columns
        WHERE table_schema = 'meeting'
          AND table_name = 'meeting_tracks'
          AND column_name = 'meeting_participant_id'
    ) THEN
        ALTER TABLE meeting.meeting_tracks
            RENAME COLUMN meeting_participant_id TO rtc_stream_participant_id;
    END IF;

    IF EXISTS (
        SELECT 1
        FROM pg_constraint c
        JOIN pg_namespace n ON n.oid = c.connamespace
        WHERE n.nspname = 'meeting'
          AND c.conname = 'meeting_invitations_meeting_room_id_fkey'
    ) THEN
        ALTER TABLE meeting.rtc_session_revocations
            RENAME CONSTRAINT meeting_invitations_meeting_room_id_fkey
            TO rtc_session_revocations_meeting_room_id_fkey;
    END IF;

    IF EXISTS (
        SELECT 1
        FROM pg_constraint c
        JOIN pg_namespace n ON n.oid = c.connamespace
        WHERE n.nspname = 'meeting'
          AND c.conname = 'meeting_participants_meeting_room_id_fkey'
    ) THEN
        ALTER TABLE meeting.rtc_stream_participants
            RENAME CONSTRAINT meeting_participants_meeting_room_id_fkey
            TO rtc_stream_participants_meeting_room_id_fkey;
    END IF;

    IF EXISTS (
        SELECT 1
        FROM pg_constraint c
        JOIN pg_namespace n ON n.oid = c.connamespace
        WHERE n.nspname = 'meeting'
          AND c.conname = 'meeting_tracks_meeting_participant_id_fkey'
    ) THEN
        ALTER TABLE meeting.meeting_tracks
            RENAME CONSTRAINT meeting_tracks_meeting_participant_id_fkey
            TO meeting_tracks_rtc_stream_participant_id_fkey;
    END IF;
END $$;

DROP TABLE IF EXISTS meeting.poll_votes;
DROP TABLE IF EXISTS meeting.poll_options;
DROP TABLE IF EXISTS meeting.polls;

DROP TABLE IF EXISTS meeting.question_votes;
DROP TABLE IF EXISTS meeting.questions;

DROP TABLE IF EXISTS meeting.breakout_assignments;
DROP TABLE IF EXISTS meeting.breakout_sessions;
