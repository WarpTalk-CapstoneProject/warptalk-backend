-- WT-824: keep WHY a recording failed, on the recording's own row.
--
-- Production has made two recordings, ever, and both are FAILED with no file. LiveKit's egress
-- runs in LiveKit Cloud, so its logs are not ours to read, and the reason it reported
-- (EgressInfo.status / EgressInfo.error) existed only in one meeting-service log line that the
-- next deploy deleted. The row said FAILED and nothing else — a dead end for the host and for
-- whoever has to fix it.
--
-- Its own column, not `content`: the download endpoint serves `content` as the artifact's body,
-- so a reason written there would download as the recording itself.
--
-- Nullable, no default, no backfill: the two existing FAILED rows' reasons are already gone.
ALTER TABLE translation_room.translation_room_artifacts
    ADD COLUMN IF NOT EXISTS failure_reason text NULL;

COMMENT ON COLUMN translation_room.translation_room_artifacts.failure_reason IS
    'Why a recording ended with no file: host-facing reason plus LiveKit egress status/error (URLs redacted). Set only when status is FAILED.';
