-- WT-473: the wall-clock instant a transcript's offsets are measured from.
--
-- transcript_segments.start_time_ms is a DURATION. Its origin — the first audio chunk the STT
-- pipeline saw for the meeting — lived only in Redis:
--
--     translationRoom:{id}:transcript_anchor_ms    TTL 6h
--
-- Production Redis runs allkeys-lru, so it EVICTS live meeting state rather than erroring, and the
-- key expires six hours after the meeting regardless. Nothing wrote it to Postgres, so once a
-- meeting was over the transcript's origin was gone and its offsets could no longer be lined up
-- against anything outside the transcript.
--
-- That is what made "click a transcript line, seek the recording" impossible: the recording's origin
-- is on translation_room_artifacts.recording_started_at, and this is the other half of the pair.
--
-- NULLABLE and set once. Transcripts that already exist have no recoverable anchor, and a consumer
-- must read NULL as "cannot align" rather than substituting the transcript's created_at — that
-- would be off by however long the meeting waited for its first word, which is exactly the kind of
-- error that renders as a plausible seek.
ALTER TABLE transcript.transcripts
    ADD COLUMN IF NOT EXISTS timeline_anchor_at timestamptz NULL;

COMMENT ON COLUMN transcript.transcripts.timeline_anchor_at IS
    'WT-473: UTC instant that transcript_segments.start_time_ms values are measured from (the first audio chunk the STT pipeline saw). Set once, from STTResultMessage.anchor_ms. NULL for transcripts predating this column — treat NULL as cannot-align, never as created_at.';
