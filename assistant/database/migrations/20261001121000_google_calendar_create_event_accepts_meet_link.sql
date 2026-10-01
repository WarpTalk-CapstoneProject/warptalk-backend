-- GMCAL1001: google_calendar_create_event can attach an existing Google Meet link.
--
-- WHY
--   The Meet plugin now creates its meeting through the Meet API only (20261001120000) and no
--   longer writes a Calendar event. When the user also wants the meeting on their calendar, the
--   Calendar plugin books it, using only the Calendar API: the gateway inserts the event with
--   conferenceData pointing at the existing Meet conference (conferenceDataVersion=1) and always
--   also puts the link in location/description, retrying once without conferenceData if Calendar
--   refuses it. For that the tool's contract has to advertise the new inputs.
--
-- WHAT IS REWRITTEN
--   Only the google_calendar_create_event element of tools_json, and within it only
--   parameters.properties, which gains the OPTIONAL meetLink, meetingCode, attendees and timeZone.
--   Existing properties and parameters.required (summary, start, end) are kept, as is every other
--   key on the element and every other tool; the array is rebuilt WITH ORDINALITY so order is
--   preserved. Both google_calendar (the live owner since 20260907100000) and the retired
--   google_workspace row are patched, like 20260917120000 did for the Meet tool.
--
--   The tool's description is only replaced while it still holds the text 20260823090000 seeded:
--   tools are operator-editable through PUT catalog/{key}/tools, and a curated text stays.
--
-- EFFECT ON USERS ALREADY CONNECTED
--   None. The tool's requiredScopes (calendar.events) and the plugin's required_scopes_json are
--   unchanged, so nobody is asked to reconnect.
--
-- IDEMPOTENT
--   The rewrite is deterministic and the WHERE only matches rows whose tools_json would actually
--   change, so a re-run writes nothing and does not churn updated_at.

WITH new_properties (props) AS (
    SELECT jsonb_build_object(
        'meetLink', jsonb_build_object('type', 'string', 'description', 'Existing Google Meet join link (for example from google_calendar_create_meet_event) to attach to the event.'),
        'meetingCode', jsonb_build_object('type', 'string', 'description', 'Meeting code of meetLink, for example abc-mnop-xyz. Derived from meetLink when omitted.'),
        'attendees', jsonb_build_object(
            'type', 'array',
            'items', jsonb_build_object('type', 'string', 'format', 'email'),
            'description', 'Guest emails. Invitations are sent when present.'
        ),
        'timeZone', jsonb_build_object('type', 'string', 'description', 'IANA time zone, for example Asia/Bangkok.')
    )
),
patched AS (
    SELECT
        p.id,
        (
            SELECT jsonb_agg(
                CASE
                    WHEN elem.tool ->> 'name' = 'google_calendar_create_event' THEN
                        elem.tool
                        || jsonb_build_object(
                            'parameters',
                            COALESCE(elem.tool -> 'parameters', '{}'::jsonb)
                            || jsonb_build_object(
                                'properties',
                                COALESCE(elem.tool #> '{parameters,properties}', '{}'::jsonb) || np.props
                            )
                        )
                        || CASE
                            WHEN elem.tool ->> 'description' = 'Create an event in the connected Google Calendar account after user confirmation.'
                                THEN jsonb_build_object(
                                    'description',
                                    'Create an event in the connected Google Calendar account after user confirmation. Pass meetLink to attach an existing Google Meet link to the event.'
                                )
                            ELSE '{}'::jsonb
                        END
                    ELSE elem.tool
                END
                ORDER BY elem.ord
            )
            FROM jsonb_array_elements(p.tools_json) WITH ORDINALITY AS elem(tool, ord)
        ) AS tools_json
    FROM assistant.plugins AS p
    CROSS JOIN new_properties AS np
    WHERE p.plugin_key IN ('google_calendar', 'google_workspace')
      AND jsonb_typeof(p.tools_json) = 'array'
      AND EXISTS (
          SELECT 1
          FROM jsonb_array_elements(p.tools_json) AS tool
          WHERE tool ->> 'name' = 'google_calendar_create_event'
      )
)
UPDATE assistant.plugins AS target
SET
    tools_json = patched.tools_json,
    updated_at = now()
FROM patched
WHERE target.id = patched.id
  AND target.tools_json IS DISTINCT FROM patched.tools_json;
