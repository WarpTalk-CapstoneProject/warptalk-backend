-- Groups the google_workspace tools by underlying Google product so the frontend can render one
-- catalog tile per product (Drive, Calendar) instead of one combined tile, without hardcoding any
-- provider-specific logic in the frontend: the grouping is data the catalog already carries.
--
-- The plugin keeps a single row, a single install/installation, and a single OAuth connection --
-- Google's own consent screen already lets a user grant Drive without Calendar (or vice versa) via
-- its per-scope checkboxes, and McpToolOrchestrator already gates each tool call against the
-- connection's actually-granted scopes (PluginConstants.ErrorCodes.MissingScope). resourceKey lets
-- the frontend show each product's tile as connected only when that product's scope was granted,
-- instead of showing every tile as "Connected" off one shared connection status.
UPDATE assistant.plugins
SET tools_json = (
    SELECT jsonb_agg(
        tool || CASE tool ->> 'name'
            WHEN 'google_drive_search' THEN jsonb_build_object(
                'resourceKey', 'drive',
                'resourceLabel', 'Google Drive',
                'resourceAvatarUrl', '/assets/plugins/google-drive.svg'
            )
            WHEN 'google_drive_get_file' THEN jsonb_build_object(
                'resourceKey', 'drive',
                'resourceLabel', 'Google Drive',
                'resourceAvatarUrl', '/assets/plugins/google-drive.svg'
            )
            WHEN 'google_calendar_list_events' THEN jsonb_build_object(
                'resourceKey', 'calendar',
                'resourceLabel', 'Google Calendar',
                'resourceAvatarUrl', '/assets/plugins/google-calendar.svg'
            )
            WHEN 'google_calendar_create_event' THEN jsonb_build_object(
                'resourceKey', 'calendar',
                'resourceLabel', 'Google Calendar',
                'resourceAvatarUrl', '/assets/plugins/google-calendar.svg'
            )
            ELSE '{}'::jsonb
        END
    )
    FROM jsonb_array_elements(tools_json) AS tool
)
WHERE plugin_key = 'google_workspace';
