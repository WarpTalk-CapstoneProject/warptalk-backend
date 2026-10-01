-- Migration: 20261001120000_add_segment_far_speaker
-- Ticket: bridge far-side speaker names (Google Meet bridge, post-meeting relabel)
-- Description:
--
-- An EXTERNAL_BRIDGE room hears the whole Google Meet side as ONE mixed stream, published by the
-- stand-in identity 00000000-0000-0000-0000-00000000b21d. Every segment of that stream used to be
-- stored with speaker_name = the stand-in's GUID; new rows are now written as
-- "Google Meet participants" (old rows are NOT migrated — the web maps them on read).
--
-- After the meeting, TranscriptService imports Google Meet's own speaker-attributed transcript and
-- names each stand-in segment after the Meet participant who said it. speaker_name keeps carrying
-- the display name (every reader already shows it); these columns record the identity behind it
-- and how it was decided, so a later host correction can win and a re-run can tell its own work
-- from a person's:
--
--   far_speaker_key         the Meet participant resource name
--                           (conferenceRecords/{id}/participants/{id}) or a host-chosen key.
--   far_speaker_source      'google_transcript' | 'host' | NULL (not attributed).
--   far_speaker_confidence  for google_transcript, the fraction of the segment's time the chosen
--                           Meet transcript entry covers (0..1).
--
-- Forward-only, idempotent, nullable with no default: existing rows read as "not attributed".

ALTER TABLE transcript.transcript_segments
    ADD COLUMN IF NOT EXISTS far_speaker_key text NULL,
    ADD COLUMN IF NOT EXISTS far_speaker_source text NULL,
    ADD COLUMN IF NOT EXISTS far_speaker_confidence real NULL;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'transcript_segments_far_speaker_source_check'
          AND conrelid = 'transcript.transcript_segments'::regclass
    ) THEN
        ALTER TABLE transcript.transcript_segments
            ADD CONSTRAINT transcript_segments_far_speaker_source_check
            CHECK (far_speaker_source IS NULL OR far_speaker_source IN ('google_transcript', 'host'));
    END IF;
END $$;

COMMENT ON COLUMN transcript.transcript_segments.far_speaker_key IS
    'EXTERNAL_BRIDGE stand-in segments: the Google Meet participant (conferenceRecords/{id}/participants/{id}) or host-chosen key this segment is attributed to. speaker_name carries its display name.';
COMMENT ON COLUMN transcript.transcript_segments.far_speaker_source IS
    'Who attributed far_speaker_key: google_transcript (automatic, from Meet''s transcript) or host. NULL = not attributed.';
COMMENT ON COLUMN transcript.transcript_segments.far_speaker_confidence IS
    'google_transcript only: overlap ratio (0..1) between the segment and the chosen Meet transcript entry.';
