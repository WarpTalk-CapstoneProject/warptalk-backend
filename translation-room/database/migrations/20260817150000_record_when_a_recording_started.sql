-- WT-473: when a recording STARTED.
--
-- translation_room_artifacts had exactly one timestamp, created_at, and
-- RecordingCompletedEventProcessor sets it from the event's OccurredAt — the moment egress
-- FINISHED. That is the wrong end of the interval.
--
-- It matters because "click a transcript line and seek the video" needs the two clocks reconciled.
-- Transcript offsets (transcript_segments.start_time_ms) are measured from the first audio chunk
-- the STT pipeline saw, while a recording begins whenever the host switched it on. Without a
-- recording origin the offset between them is unknown and varies per meeting, so every seek lands
-- somewhere plausible and wrong.
--
-- NULLABLE, and it stays nullable. Recordings made before this column existed have no start time
-- and none can be reconstructed: LiveKit's egress info is not retained, and the value was simply
-- discarded. The UI must read null as "not seekable" rather than substituting zero, which would be
-- a silently incorrect seek on every historical recording.
ALTER TABLE translation_room.translation_room_artifacts
    ADD COLUMN IF NOT EXISTS recording_started_at timestamptz NULL;

COMMENT ON COLUMN translation_room.translation_room_artifacts.recording_started_at IS
    'WT-473: UTC instant the recording began, from LiveKit EgressInfo.started_at. NULL for artifacts that are not recordings, and for recordings predating this column — treat NULL as not-seekable, never as zero.';
