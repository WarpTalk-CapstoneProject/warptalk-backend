-- WarpBot: "create a meeting with @Google Meet" should just produce a link.
--
-- WHY
--   google_calendar_create_meet_event declared required: [summary, start, end], so the model
--   interrogated the user for a title and times before it would create anything. The gateway now
--   defaults all three (summary "Google Meet meeting", start = now truncated to the minute,
--   end = start + 30 minutes) and returns the meeting code alongside the join link, so the
--   contract advertised to the model drops its required list and says how omission behaves.
--
-- WHAT IS REWRITTEN
--   Only the google_calendar_create_meet_event element of tools_json, and within it only
--   `description` and the `parameters` object. Every other key on that element (name, pluginKey,
--   label, effect, requiredScopes, ...) and every other tool in the array is left exactly as it
--   is. The array is rebuilt WITH ORDINALITY so element order is preserved.
--
--   Both google_meet (the live owner since 20260907100000) and the retired google_workspace row
--   are patched: the split copied tool bodies out of google_workspace without removing them, so
--   the legacy row still carries the old contract and would hand it back to anyone who reads it.
--
-- DISPLAY TEXT
--   The google_meet row's `description` column is operator-editable through the admin catalog
--   API, so it is only replaced while it still holds the text 20260907100000 seeded. A curated
--   description is left alone.
--
-- IDEMPOTENT
--   The rewrite is deterministic and the WHERE only matches rows whose tools_json would actually
--   change, so a re-run writes nothing and does not churn updated_at.

WITH patched AS (
    SELECT
        p.id,
        (
            SELECT jsonb_agg(
                CASE
                    WHEN elem.tool ->> 'name' = 'google_calendar_create_meet_event' THEN
                        elem.tool || jsonb_build_object(
                            'description',
                            'Create a Google Meet meeting (hosted on Google Meet, NOT a WarpTalk room) as a Google Calendar event and return its join link and meeting code. Omit start/end to start the meeting right now for 30 minutes. Only use when the user asks for Google Meet.',
                            'parameters',
                            jsonb_build_object(
                                'type', 'object',
                                'properties', jsonb_build_object(
                                    'summary', jsonb_build_object('type', 'string', 'description', 'Meeting title. Omit for a default title.'),
                                    'start', jsonb_build_object('type', 'string', 'description', 'RFC3339 start date-time. Omit to start now.'),
                                    'end', jsonb_build_object('type', 'string', 'description', 'RFC3339 end date-time. Omit for start + 30 minutes.'),
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

UPDATE assistant.plugins
SET
    description = 'Create a Google Meet meeting and get its join link and meeting code.',
    updated_at = now()
WHERE plugin_key = 'google_meet'
  AND description = 'Schedule a meeting with a Google Meet link attached.';
