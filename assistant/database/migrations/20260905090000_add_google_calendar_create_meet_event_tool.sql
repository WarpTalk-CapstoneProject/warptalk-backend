-- Adds the curated native Google Calendar action that creates an event with a Google Meet
-- conference attached. The gateway owns the Google-specific adapter code; the catalog only
-- advertises the tool contract that WarpBot can choose dynamically.
UPDATE assistant.plugins
SET tools_json = CASE
    WHEN EXISTS (
        SELECT 1
        FROM jsonb_array_elements(tools_json) AS tool
        WHERE tool ->> 'name' = 'google_calendar_create_meet_event'
    ) THEN tools_json
    ELSE tools_json || jsonb_build_array(
        jsonb_build_object(
            'name', 'google_calendar_create_meet_event',
            'pluginKey', 'google_workspace',
            'label', 'Create Google Meet meeting',
            'description', 'Create a Google Calendar event with a Google Meet link after user confirmation.',
            'effect', 'write',
            'requiredScopes', jsonb_build_array('https://www.googleapis.com/auth/calendar.events'),
            'resourceKey', 'calendar',
            'resourceLabel', 'Google Calendar',
            'resourceAvatarUrl', '/assets/plugins/google-calendar.svg',
            'parameters', jsonb_build_object(
                'type', 'object',
                'properties', jsonb_build_object(
                    'summary', jsonb_build_object('type', 'string'),
                    'start', jsonb_build_object('type', 'string', 'description', 'RFC3339 start date-time.'),
                    'end', jsonb_build_object('type', 'string', 'description', 'RFC3339 end date-time.'),
                    'timeZone', jsonb_build_object('type', 'string', 'description', 'IANA time zone, for example Asia/Bangkok.'),
                    'description', jsonb_build_object('type', 'string'),
                    'attendees', jsonb_build_object(
                        'type', 'array',
                        'items', jsonb_build_object('type', 'string', 'format', 'email')
                    )
                ),
                'required', jsonb_build_array('summary', 'start', 'end')
            )
        )
    )
END
WHERE plugin_key = 'google_workspace';
