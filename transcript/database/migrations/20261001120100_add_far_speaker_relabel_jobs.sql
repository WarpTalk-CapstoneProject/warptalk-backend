-- Migration: 20261001120100_add_far_speaker_relabel_jobs
-- Ticket: bridge far-side speaker names (Google Meet bridge, post-meeting relabel)
-- Description:
--
-- One row per EXTERNAL_BRIDGE room whose far side needs naming after the meeting. The relabel
-- worker discovers rooms from their stand-in segments, waits for the room to end, then asks
-- AssistantService (which holds the host's Google grant) for Google Meet's transcript entries.
-- Google produces them some minutes after the conference ends and keeps them 30 days, so the job
-- is retried with backoff (capped at 2 hours) and given up 30 days after the room ended.
--
-- status: pending | done | no_transcript | abandoned | skipped
--   done           relabel applied (possibly to zero segments).
--   no_transcript  the conference ended long enough ago and Meet never produced a transcript
--                  (transcription was off).
--   abandoned      30 days passed without a readable transcript (or grant).
--   skipped        not a bridge room / no Meet link / no stand-in segments.
--
-- Forward-only and idempotent.

CREATE TABLE IF NOT EXISTS transcript.far_speaker_relabel_jobs (
    translation_room_id  uuid        NOT NULL,
    status               text        NOT NULL DEFAULT 'pending',
    attempts             integer     NOT NULL DEFAULT 0,
    next_attempt_at      timestamptz NOT NULL DEFAULT now(),
    room_ended_at        timestamptz NULL,
    last_error           text        NULL,
    segments_relabeled   integer     NULL,
    completed_at         timestamptz NULL,
    created_at           timestamptz NOT NULL DEFAULT now(),
    updated_at           timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT far_speaker_relabel_jobs_pkey PRIMARY KEY (translation_room_id),
    CONSTRAINT far_speaker_relabel_jobs_status_check
        CHECK (status IN ('pending', 'done', 'no_transcript', 'abandoned', 'skipped'))
);

CREATE INDEX IF NOT EXISTS far_speaker_relabel_jobs_due_idx
    ON transcript.far_speaker_relabel_jobs (next_attempt_at)
    WHERE status = 'pending';

COMMENT ON TABLE transcript.far_speaker_relabel_jobs IS
    'Post-meeting relabel of EXTERNAL_BRIDGE stand-in segments from Google Meet''s speaker-attributed transcript. translation_room_id is an external TranslationRoomService id (no physical FK).';
