-- WT-646: turn the one google_workspace catalog row into three independent product rows --
-- google_drive, google_calendar and google_meet.
--
-- WHY
--   20260826130000 stamped resourceKey/resourceLabel/resourceAvatarUrl onto each tool so the
--   frontend could render one tile per Google product off a single catalog row. That was a display
--   trick: underneath there was still one plugin, one installation and one install/uninstall
--   button. A user who wanted Calendar but not Drive could decline the Drive scope on Google's
--   consent screen, but could not remove Drive from their assistant -- the tile was a view of a
--   row they did not own separately. The product decision is that Drive, Calendar and Meet are
--   three apps a user installs and removes independently, so they become three rows and the
--   display-split mechanism goes away with this migration.
--
--   The tool objects therefore lose resourceKey/resourceLabel/resourceAvatarUrl: the grouping they
--   encoded is now the plugin row itself, and leaving them behind would leave two competing
--   sources of truth for "which product does this tool belong to". Each tool's pluginKey is
--   rewritten to its new owner, because pluginKey is what the orchestrator resolves a tool call
--   back to a plugin (and therefore to an installation and a scope check) with.
--
-- WHERE THE TOOLS GO
--   The mapping below reproduces the resourceKey grouping that 20260826130000 established, with
--   one carve-out: google_calendar_create_meet_event carries resourceKey 'calendar' but is the
--   only tool Google Meet has, so it moves to google_meet rather than staying with Calendar. That
--   is why this switches on tool name rather than on resourceKey -- the name is exact, and the
--   Meet carve-out cannot be expressed in the old grouping at all.
--
--   The tool bodies are read out of the live google_workspace row rather than re-authored here, so
--   any parameter-schema patch applied since 20260823090000 (google_drive_get_file from
--   20260825100000, google_calendar_create_meet_event from 20260905090000) travels with the tool
--   instead of being silently reverted by this migration.
--
-- REQUIRED SCOPES
--   Each row now asks for only what its own tools call. Drive keeps drive.readonly; Calendar and
--   Meet both need calendar.events, because a Meet conference is created as a Calendar event with
--   a conferenceData request attached -- there is no separate Meet scope to ask for. That overlap
--   is deliberate and is exactly why 20260907101000 keys the OAuth grant by provider instead of by
--   plugin: a user who installs both should consent once, not twice for the same scope.
--
-- WHAT HAPPENS TO EXISTING USERS
--   Installations are duplicated onto all three new rows below, so nobody who had the combined
--   plugin installed loses a tool the morning this ships.
--
--   Connections are deliberately NOT duplicated. 20260907101000 re-keys plugin_connections by
--   (user_id, provider), so copying the existing Google grant onto three plugin ids would
--   manufacture exactly the duplicates that migration then has to merge and delete -- three copies
--   of one encrypted refresh token, two of which get thrown away by a tie-break rule. The single
--   existing connection row stays where it is, on the google_workspace plugin id; after
--   20260907101000 stamps provider='google' on it, it serves all three products, and its plugin_id
--   is provenance only ("this grant was first obtained through that row").
--
-- WHY google_workspace IS NOT DELETED
--   plugin_installations, plugin_connections, plugin_confirmation_tokens and plugin_tool_audits all
--   reference plugins(id) ON DELETE CASCADE. A DELETE here would take every historical audit row,
--   every user's Google refresh token, and the installations we just read from, with it. The row is
--   deactivated instead: is_active=false keeps it out of the catalog the API serves while leaving
--   the foreign keys intact.

WITH legacy AS (
    SELECT tools_json
    FROM assistant.plugins
    WHERE plugin_key = 'google_workspace'
),
split AS (
    SELECT
        CASE tool ->> 'name'
            WHEN 'google_drive_search' THEN 'google_drive'
            WHEN 'google_drive_get_file' THEN 'google_drive'
            WHEN 'google_calendar_list_events' THEN 'google_calendar'
            WHEN 'google_calendar_create_event' THEN 'google_calendar'
            WHEN 'google_calendar_create_meet_event' THEN 'google_meet'
        END AS target_key,
        -- The display-split fields go away with the mechanism they fed.
        tool - 'resourceKey' - 'resourceLabel' - 'resourceAvatarUrl' AS tool_body
    FROM legacy, jsonb_array_elements(legacy.tools_json) AS tool
),
grouped AS (
    SELECT
        target_key,
        -- Ordered so a re-run produces a byte-identical array and the ON CONFLICT branch below is
        -- genuinely a no-op rather than a rewrite.
        jsonb_agg(
            tool_body || jsonb_build_object('pluginKey', target_key)
            ORDER BY tool_body ->> 'name'
        ) AS tools_json
    FROM split
    -- An unrecognised tool name maps to NULL and is left on the legacy row rather than guessed at.
    WHERE target_key IS NOT NULL
    GROUP BY target_key
),
new_rows (id, plugin_key, label, description, avatar_url, required_scopes_json) AS (
    VALUES
        (
            'd1a5f3c0-6b21-4f7e-9c84-2f0f1a7d4e11'::uuid,
            'google_drive',
            'Google Drive',
            'Search your Google Drive and read the contents of a file.',
            '/assets/plugins/google-drive.svg',
            '["https://www.googleapis.com/auth/drive.readonly"]'::jsonb
        ),
        (
            'c2b6e4d1-7c32-4a8f-8d95-3a1e2b8c5f22'::uuid,
            'google_calendar',
            'Google Calendar',
            'List events on your Google Calendar and create new ones.',
            '/assets/plugins/google-calendar.svg',
            '["https://www.googleapis.com/auth/calendar.events"]'::jsonb
        ),
        (
            '9e3c7a52-8d43-4b90-ae06-4b2f3c9d6a33'::uuid,
            'google_meet',
            'Google Meet',
            'Schedule a meeting with a Google Meet link attached.',
            '/assets/plugins/google-meet.svg',
            '["https://www.googleapis.com/auth/calendar.events"]'::jsonb
        )
)
INSERT INTO assistant.plugins (
    id,
    plugin_key,
    label,
    description,
    avatar_url,
    provider,
    kind,
    required_scopes_json,
    tools_json,
    is_active
)
SELECT
    n.id,
    n.plugin_key,
    n.label,
    n.description,
    n.avatar_url,
    'google',
    -- All three keep the hand-written GoogleWorkspaceOAuthClient / Google tool gateway that
    -- google_workspace used; none of them is a remote MCP server.
    'native',
    n.required_scopes_json,
    COALESCE(g.tools_json, '[]'::jsonb),
    true
FROM new_rows AS n
LEFT JOIN grouped AS g ON g.target_key = n.plugin_key
-- The conflict branch repairs the two columns an operator can never have authored, and touches
-- nothing else.
--
-- provider and kind are structural: they are what 20260907101000 keys a user's OAuth grant by and
-- what the orchestrator dispatches on, no admin endpoint writes either, and a row carrying the
-- wrong one is broken rather than merely differently curated. Repairing those is this migration
-- re-asserting its own decision.
--
-- label, description, avatar_url, required_scopes_json and tools_json are not structural: every
-- one of them is editable through the admin catalog API that 20260907102000 exists to support, and
-- tools_json is editable tool by tool through PUT catalog/{key}/tools. Copying them back off the
-- legacy google_workspace row would silently revert an operator's curation to a snapshot of a row
-- this very migration retires -- and would revert it again on every subsequent re-run, with no
-- trace of what was overwritten. The same reasoning already kept `is_active` out of this branch
-- (20260907102000 hands operators a portal that can deactivate a row, and a re-run must not undo
-- that); it applies with equal force to the other five. Creating these rows is this migration's
-- job; deciding what they say afterwards is not.
--
-- The WHERE is what makes a genuine re-run a true no-op rather than an updated_at churn: with the
-- structural columns already correct, the branch writes nothing at all.
ON CONFLICT (plugin_key) DO UPDATE SET
    provider = EXCLUDED.provider,
    kind = EXCLUDED.kind,
    updated_at = now()
WHERE assistant.plugins.provider IS DISTINCT FROM EXCLUDED.provider
   OR assistant.plugins.kind IS DISTINCT FROM EXCLUDED.kind;

-- Carry every existing google_workspace installation onto all three product rows. A user who had
-- the combined plugin installed had all five tools available, so all three successors start
-- installed -- the alternative is a silent capability loss on deploy day.
--
-- status, installed_at and config_json are preserved verbatim. disabled_at travels with them too:
-- status='disabled' with a NULL disabled_at is a state the domain never produces, and splitting
-- the pair here would invent one.
--
-- Idempotent by way of the NOT EXISTS guard against the (user_id, plugin_id) unique key, so a
-- re-run inserts nothing and does not mint fresh installation ids.
INSERT INTO assistant.plugin_installations (
    id,
    user_id,
    plugin_id,
    status,
    config_json,
    installed_at,
    disabled_at
)
SELECT
    gen_random_uuid(),
    src.user_id,
    target.id,
    src.status,
    src.config_json,
    src.installed_at,
    src.disabled_at
FROM assistant.plugin_installations AS src
JOIN assistant.plugins AS legacy
    ON legacy.id = src.plugin_id
   AND legacy.plugin_key = 'google_workspace'
JOIN assistant.plugins AS target
    ON target.plugin_key IN ('google_drive', 'google_calendar', 'google_meet')
WHERE NOT EXISTS (
    SELECT 1
    FROM assistant.plugin_installations AS existing
    WHERE existing.user_id = src.user_id
      AND existing.plugin_id = target.id
);

-- Retire the combined row. The `AND is_active` guard keeps a re-run from touching updated_at.
UPDATE assistant.plugins
SET
    is_active = false,
    updated_at = now()
WHERE plugin_key = 'google_workspace'
  AND is_active;
