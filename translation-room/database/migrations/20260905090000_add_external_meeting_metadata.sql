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
