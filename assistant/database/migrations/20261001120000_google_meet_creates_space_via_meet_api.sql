-- GMCAL1001: the Google Meet plugin calls only the Meet API.
--
-- WHY
--   google_calendar_create_meet_event used to create a Google Calendar event with a
--   conferenceData.createRequest (scope calendar.events), so connecting only Meet wrote to the
--   user's Calendar. The product rule is that each Google plugin calls only its own Google API. The
--   gateway now creates the meeting with Meet REST v2 spaces.create, which needs
--   https://www.googleapis.com/auth/meetings.space.created and nothing from Calendar. Putting the
--   meeting on a calendar is the Calendar plugin's job (google_calendar_create_event with meetLink).
--
-- WHAT CHANGES
--   1. google_meet.required_scopes_json: calendar.events is removed, meetings.space.created is
--      added, meetings.space.readonly is kept (HostMeetConferenceService / MeetConferenceEndWorker
--      read conferenceRecords with it). Every other scope and the existing order are kept.
--   2. The google_calendar_create_meet_event element of tools_json: requiredScopes becomes
--      [meetings.space.created], and description + parameters say that a Meet link is created, no
--      calendar event, and that summary/start/end/... are only echoed back. The tool KEY is kept:
--      tool policies, AI prompts and tests reference it. Other keys on the element and every other
--      tool are left as they are; the array is rebuilt WITH ORDINALITY so order is preserved.
--      Like 20260917120000, both google_meet and the retired google_workspace row are patched so
--      nobody reading the legacy row gets the old contract back.
--
-- EFFECT ON USERS ALREADY CONNECTED
--   Their stored Google grant lacks meetings.space.created, so Meet's requiredScopes is no longer a
--   subset of the grant and the plugins page / WarpBot read Meet as "connect it" instead of
--   Connected. Connect goes back to Google with Meet's scopes and include_granted_scopes=true, so
--   the consent is incremental and Calendar/Drive keep what they had. Until then a Meet tool call
--   is refused with missing_scope (Google answers 403 ACCESS_TOKEN_SCOPE_INSUFFICIENT), never a
--   silent Calendar write. Removing calendar.events from Meet does not revoke it from the grant: a
--   user who also has Calendar keeps it there.
--
-- IDEMPOTENT
--   Both rewrites are deterministic and each UPDATE only matches a row whose value would actually
--   change, so a re-run writes nothing and does not churn updated_at.

WITH wanted (scope, ord) AS (
    VALUES
        ('https://www.googleapis.com/auth/meetings.space.created', 1),
        ('https://www.googleapis.com/auth/meetings.space.readonly', 2)
),
meet_row AS (
    SELECT p.id, COALESCE(p.required_scopes_json, '[]'::jsonb) AS scopes
    FROM assistant.plugins AS p
    WHERE p.plugin_key = 'google_meet'
      AND jsonb_typeof(COALESCE(p.required_scopes_json, '[]'::jsonb)) = 'array'
),
patched AS (
    SELECT
        s.id,
        COALESCE(
            (
                SELECT jsonb_agg(elem.scope ORDER BY elem.ord)
                FROM jsonb_array_elements(s.scopes) WITH ORDINALITY AS elem(scope, ord)
                WHERE elem.scope <> to_jsonb('https://www.googleapis.com/auth/calendar.events'::text)
            ),
            '[]'::jsonb
        ) || COALESCE(
            (
                SELECT jsonb_agg(to_jsonb(w.scope) ORDER BY w.ord)
                FROM wanted AS w
                WHERE NOT (s.scopes ? w.scope)
            ),
            '[]'::jsonb
        ) AS scopes
    FROM meet_row AS s
)
UPDATE assistant.plugins AS p
SET
    required_scopes_json = patched.scopes,
    updated_at = now()
FROM patched
WHERE p.id = patched.id
  AND p.required_scopes_json IS DISTINCT FROM patched.scopes;

WITH patched AS (
    SELECT
        p.id,
        (
            SELECT jsonb_agg(
                CASE
                    WHEN elem.tool ->> 'name' = 'google_calendar_create_meet_event' THEN
                        elem.tool || jsonb_build_object(
                            'requiredScopes',
                            jsonb_build_array('https://www.googleapis.com/auth/meetings.space.created'),
                            'description',
                            'Create a Google Meet meeting link (hosted on Google Meet, NOT a WarpTalk room) through the Google Meet API and return its join link and meeting code. This does NOT create a calendar event: Meet stores no title or time. summary/start/end/timeZone/description/attendees are only echoed back so the meeting can then be added to Google Calendar with google_calendar_create_event and meetLink. Only use when the user asks for Google Meet.',
                            'parameters',
                            jsonb_build_object(
                                'type', 'object',
                                'properties', jsonb_build_object(
                                    'summary', jsonb_build_object('type', 'string', 'description', 'Meeting title, echoed back for a calendar event. Omit for a default title.'),
                                    'start', jsonb_build_object('type', 'string', 'description', 'RFC3339 start date-time, echoed back for a calendar event. Not stored by Meet.'),
                                    'end', jsonb_build_object('type', 'string', 'description', 'RFC3339 end date-time, echoed back for a calendar event. Not stored by Meet.'),
                                    'timeZone', jsonb_build_object('type', 'string', 'description', 'IANA time zone, for example Asia/Bangkok.'),
                                    'description', jsonb_build_object('type', 'string'),
                                    'attendees', jsonb_build_object(
                                        'type', 'array',
                                        'items', jsonb_build_object('type', 'string', 'format', 'email')
                                    )
                                ),
                                'required', '[]'::jsonb
                            )
                        )
                    ELSE elem.tool
                END
                ORDER BY elem.ord
            )
            FROM jsonb_array_elements(p.tools_json) WITH ORDINALITY AS elem(tool, ord)
        ) AS tools_json
    FROM assistant.plugins AS p
    WHERE p.plugin_key IN ('google_meet', 'google_workspace')
      AND jsonb_typeof(p.tools_json) = 'array'
      AND EXISTS (
          SELECT 1
          FROM jsonb_array_elements(p.tools_json) AS tool
          WHERE tool ->> 'name' = 'google_calendar_create_meet_event'
      )
)
UPDATE assistant.plugins AS target
SET
    tools_json = patched.tools_json,
    updated_at = now()
FROM patched
WHERE target.id = patched.id
  AND target.tools_json IS DISTINCT FROM patched.tools_json;
