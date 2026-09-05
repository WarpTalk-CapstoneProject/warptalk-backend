-- Migration: 20260903134800_add_transcript_pause_windows
-- Ticket: WT-605
-- Description:
--
-- "Pause Transcript" — the host can stop the transcript from being written down without
-- touching translation, dubbing, subtitles or LiveKit. Those keep running unaffected because
-- they never read this table or this service's database at all; this row exists purely so the
-- transcript panel can draw a "Transcript paused · HH:MM–HH:MM" divider, exactly the display-only
-- role translation_room.translation_room_sessions plays for "Translation N" dividers on the
-- other side of the system.
--
-- NOT the persistence gate. TranscriptRedisConsumerService gates what it writes with a Redis set
-- of skipped segment_ids (translationRoom:{roomId}:transcript_paused_segments), not by comparing
-- timestamps against this table — see IsRoomTranscriptPausedAsync. Translation/TTS result
-- messages carry no absolute wall-clock timestamp to compare against StartedAt/EndedAt with, only
-- the STT message does (anchor_ms/start_ms), so this table is read only for display.

CREATE TABLE IF NOT EXISTS transcript.transcript_pause_windows (
    id                    uuid PRIMARY KEY DEFAULT uuidv7(),
    translation_room_id   uuid NOT NULL,
    started_at            timestamptz NOT NULL,
    ended_at              timestamptz NULL,
    paused_by             uuid NOT NULL,
    resumed_by            uuid NULL,
    created_at            timestamptz NOT NULL DEFAULT now(),
    updated_at            timestamptz NOT NULL DEFAULT now()
);

COMMENT ON TABLE transcript.transcript_pause_windows IS
    'WT-605: one [Pause Transcript, Resume Transcript] window per room. Display-only metadata for the transcript panel divider — NOT the write gate (see TranscriptRedisConsumerService.IsRoomTranscriptPausedAsync).';
COMMENT ON COLUMN transcript.transcript_pause_windows.translation_room_id IS
    'External TranslationRoomService room id. No physical FK.';
COMMENT ON COLUMN transcript.transcript_pause_windows.ended_at IS
    'NULL while the window is still open (transcript currently paused for this room).';
COMMENT ON COLUMN transcript.transcript_pause_windows.paused_by IS
    'External AuthService user id (the host who paused). No physical FK.';
COMMENT ON COLUMN transcript.transcript_pause_windows.resumed_by IS
    'External AuthService user id (the host who resumed). No physical FK. NULL until resumed.';

-- Looked up on every Pause/Resume call (GetActiveWindowByRoomIdAsync) and on every panel load
-- (GetWindowsByRoomIdAsync); a partial index on the open-window case keeps the hot path cheap
-- without needing a separate lookup table for "is this room currently paused".
CREATE INDEX IF NOT EXISTS transcript_pause_windows_room_active_idx
    ON transcript.transcript_pause_windows (translation_room_id, ended_at);
