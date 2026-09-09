-- Where a bridged meeting actually lives, when it does not live in WarpTalk.
--
-- WHY THIS EXISTS
--     An EXTERNAL_BRIDGE room is the WarpTalk half of a call hosted somewhere else. Until now the
--     room knew it was a bridge but not what it was a bridge TO, so the link the user had to join
--     - the whole point of the meeting - lived nowhere. WarpBot can now create the Google Calendar
--     event and its Meet conference, and these four columns are where that comes to rest.
--
-- WHY NULLABLE, WHY ADDITIVE
--     Every existing room predates the feature and every column here is optional for rooms that
--     are not bridges, so all four are nullable and added with IF NOT EXISTS. Nothing is dropped
--     or rewritten, and re-running the file is a no-op.
--
-- THE PARTIAL INDEX
--     Lookups only ever ask "which room came from this calendar event", which is meaningless for
--     the rows where both columns are null - the overwhelming majority. Filtering them out keeps
--     the index the size of the bridge rooms rather than the size of the table.

ALTER TABLE translation_room.translation_rooms
    ADD COLUMN IF NOT EXISTS external_provider VARCHAR(40) NULL,
    ADD COLUMN IF NOT EXISTS external_meeting_url TEXT NULL,
    ADD COLUMN IF NOT EXISTS external_calendar_event_id VARCHAR(255) NULL,
    ADD COLUMN IF NOT EXISTS external_calendar_event_url TEXT NULL;

COMMENT ON COLUMN translation_room.translation_rooms.external_provider IS
    'Provider for an external bridged meeting, e.g. GOOGLE_MEET.';

COMMENT ON COLUMN translation_room.translation_rooms.external_meeting_url IS
    'Join URL for the external meeting shown alongside the WarpTalk room.';

COMMENT ON COLUMN translation_room.translation_rooms.external_calendar_event_id IS
    'Provider event id for the calendar entry that owns the external meeting.';

COMMENT ON COLUMN translation_room.translation_rooms.external_calendar_event_url IS
    'Provider URL for the calendar event that owns the external meeting.';

CREATE INDEX IF NOT EXISTS translation_rooms_external_calendar_event_idx
    ON translation_room.translation_rooms(external_provider, external_calendar_event_id)
    WHERE external_calendar_event_id IS NOT NULL;
