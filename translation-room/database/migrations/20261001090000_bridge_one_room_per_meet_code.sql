-- Migration: 20261001090000_bridge_one_room_per_meet_code
-- Created At: 2026-10-01
-- Description:
--   One shared EXTERNAL_BRIDGE room per Google Meet code (per workspace), with exactly one
--   desktop - the "capturer" - publishing the far side's mixed audio as the stand-in identity.
--
--   WHAT WAS BROKEN
--     Every WarpTalk user with the desktop app in the same Meet call got their OWN bridge room:
--     the web listed 50 rooms client-side and created one when it found no match, with nothing on
--     the server to make that idempotent. N users therefore produced N rooms, N stand-ins and N
--     copies of the far side's audio, and nobody's own voice reached anybody else's transcript.
--
--   THE COLUMNS
--     external_meeting_code        normalized Meet code (xxx-xxxx-xxx). Set by
--                                  POST /translation-rooms/bridge/claim, NOT by the ordinary
--                                  create path, so a calendar booking that reuses one Meet link
--                                  for many occurrences is never refused by the index below.
--     bridge_capturer_user_id      which participant's device publishes the far side right now.
--                                  NULL = a room from before this column: the host keeps that
--                                  authority (bridge-token, external meeting language).
--     bridge_capturer_heartbeat_at the capturer's lease. Older than 45 s means the capturer is
--                                  gone and any participant may take over.
--
--   THE UNIQUE INDEX
--     The race guard for claim: two desktops claiming the same Meet code at the same instant both
--     try to INSERT, the index rejects the loser, and the loser re-reads and joins the winner's
--     room. Partial so a finished meeting never blocks the next call on the same (reusable) Meet
--     code, and so it costs nothing for the rows that are not bridges.
--
--   BACKFILL
--     Open bridge rooms get their code from external_meeting_url. Today's client-side create
--     could leave several open rooms for one code in a workspace; only the most relevant one per
--     (workspace, code) is stamped - live before not-yet-started, newest first - so the backfill
--     itself can never violate the index. The others keep a NULL code and simply age out.
--
--     Open bridge rooms still capped at the old 2 seats are raised to 20, the new type default:
--     the stand-in plus one human was the old shape, and claim now seats every member.
--
--   Re-running the file is a no-op: every statement is IF NOT EXISTS or skips stamped rows.

ALTER TABLE translation_room.translation_rooms
    ADD COLUMN IF NOT EXISTS external_meeting_code VARCHAR(32) NULL,
    ADD COLUMN IF NOT EXISTS bridge_capturer_user_id UUID NULL,
    ADD COLUMN IF NOT EXISTS bridge_capturer_heartbeat_at TIMESTAMPTZ NULL;

COMMENT ON COLUMN translation_room.translation_rooms.external_meeting_code IS
    'Normalized Google Meet code (xxx-xxxx-xxx) of the call an EXTERNAL_BRIDGE room bridges. Set by bridge claim; unique per workspace among open bridge rooms.';

COMMENT ON COLUMN translation_room.translation_rooms.bridge_capturer_user_id IS
    'EXTERNAL_BRIDGE only: the participant whose device publishes the far side as the stand-in. NULL = legacy room, the host holds that authority. External AuthService user id, no FK.';

COMMENT ON COLUMN translation_room.translation_rooms.bridge_capturer_heartbeat_at IS
    'Last capturer heartbeat. Older than 45 seconds = stale lease; any participant may take over.';

WITH candidates AS (
    SELECT
        id,
        workspace_id,
        substring(lower(external_meeting_url) from 'meet\.google\.com/([a-z]{3,4}-[a-z]{3,4}-[a-z]{3,4})([^a-z-]|$)') AS code,
        status,
        created_at
    FROM translation_room.translation_rooms
    WHERE translation_room_type = 'EXTERNAL_BRIDGE'
      AND status NOT IN ('ENDED', 'CANCELLED', 'EXPIRED', 'FAILED')
      AND deleted_at IS NULL
      AND external_meeting_code IS NULL
      AND external_meeting_url IS NOT NULL
),
ranked AS (
    SELECT
        c.id,
        c.workspace_id,
        c.code,
        ROW_NUMBER() OVER (
            PARTITION BY c.workspace_id, c.code
            ORDER BY (c.status IN ('IN_PROGRESS', 'PAUSED', 'OPEN', 'WAITING')) DESC, c.created_at DESC
        ) AS rn
    FROM candidates c
    WHERE c.code IS NOT NULL
)
UPDATE translation_room.translation_rooms room
SET external_meeting_code = ranked.code
FROM ranked
WHERE room.id = ranked.id
  AND ranked.rn = 1
  -- Idempotence: never stamp a second room for a (workspace, code) already taken.
  AND NOT EXISTS (
      SELECT 1
      FROM translation_room.translation_rooms other
      WHERE other.workspace_id = ranked.workspace_id
        AND other.external_meeting_code = ranked.code
        AND other.translation_room_type = 'EXTERNAL_BRIDGE'
        AND other.status NOT IN ('ENDED', 'CANCELLED', 'EXPIRED', 'FAILED')
        AND other.deleted_at IS NULL
  );

UPDATE translation_room.translation_rooms
SET max_participants = 20
WHERE translation_room_type = 'EXTERNAL_BRIDGE'
  AND max_participants = 2
  AND status NOT IN ('ENDED', 'CANCELLED', 'EXPIRED', 'FAILED')
  AND deleted_at IS NULL;

CREATE UNIQUE INDEX IF NOT EXISTS translation_rooms_open_bridge_meet_code_key
    ON translation_room.translation_rooms (workspace_id, external_meeting_code)
    WHERE external_meeting_code IS NOT NULL
      AND translation_room_type = 'EXTERNAL_BRIDGE'
      AND status NOT IN ('ENDED', 'CANCELLED', 'EXPIRED', 'FAILED')
      AND deleted_at IS NULL;
