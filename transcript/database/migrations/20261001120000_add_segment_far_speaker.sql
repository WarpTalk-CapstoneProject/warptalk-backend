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
--                           (conferenceRecords/{id}/participants/{id}), a host-chosen key, or
--                           the live far_speaker_name stt_worker attached to the segment.
--   far_speaker_source      'google_transcript' | 'host' | a live source stt_worker reports
--                           (far_speaker_source on stt:results) | NULL (not attributed).
--                           Deliberately no CHECK: the live producer's vocabulary is
--                           warptalk-ai's, and a value it adds must not dead-letter segments.
--   far_speaker_confidence  for google_transcript, the fraction of the segment's time the chosen
--                           Meet transcript entry covers (0..1).
--
-- Forward-only, idempotent, nullable with no default: existing rows read as "not attributed".

ALTER TABLE transcript.transcript_segments
    ADD COLUMN IF NOT EXISTS far_speaker_key text NULL,
    ADD COLUMN IF NOT EXISTS far_speaker_source text NULL,
    ADD COLUMN IF NOT EXISTS far_speaker_confidence real NULL;

COMMENT ON COLUMN transcript.transcript_segments.far_speaker_key IS
    'EXTERNAL_BRIDGE stand-in segments: the Google Meet participant (conferenceRecords/{id}/participants/{id}) or host-chosen key this segment is attributed to. speaker_name carries its display name.';
COMMENT ON COLUMN transcript.transcript_segments.far_speaker_source IS
    'Who attributed far_speaker_key: google_transcript (post-meeting, from Meet''s transcript), host, or the live source stt_worker reported. NULL = not attributed.';
COMMENT ON COLUMN transcript.transcript_segments.far_speaker_confidence IS
    'Confidence (0..1) of the attribution: for google_transcript the overlap ratio with the chosen Meet transcript entry; for a live source the producer''s own score.';
