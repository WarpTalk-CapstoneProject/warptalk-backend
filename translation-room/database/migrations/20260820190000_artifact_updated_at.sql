-- When an artifact's CONTENT last changed.
--
-- translation_room_artifacts has exactly one timestamp, created_at, and the summary artifact's
-- `content` is rewritten in place: SummaryResultConsumerWorker replaces it whenever somebody
-- re-summarises the meeting under a different template. So a summary written at 10:00 and rewritten
-- at 14:00 still reports 10:00, and nothing anywhere records that it moved.
--
-- WHAT THAT BREAKS
--     "Is this summary out of date?" is answerable — a transcript segment carries `updated_at`, and
--     a correction moves it. Compare the newest corrected segment against the summary and you know.
--     But with a timestamp that never moves, the comparison can only ever say YES: regenerating the
--     summary would not clear the warning, so the warning would become furniture and stop meaning
--     anything. A staleness signal that cannot turn itself off is worse than none.
--
-- NULLABLE, AND STAYS NULLABLE. Artifacts written before this column existed have no honest value
-- for it and none can be reconstructed. A reader must treat NULL as "unknown", falling back to
-- created_at, rather than as "never updated" — which would be a claim.
ALTER TABLE translation_room.translation_room_artifacts
    ADD COLUMN IF NOT EXISTS updated_at timestamptz NULL;

COMMENT ON COLUMN translation_room.translation_room_artifacts.updated_at IS
    'UTC instant the artifact CONTENT last changed — moved by a summary rewrite. NULL for artifacts predating this column; read NULL as unknown and fall back to created_at.';
