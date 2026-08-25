CREATE TABLE IF NOT EXISTS assistant.plugins (
    id UUID PRIMARY KEY,
    plugin_key VARCHAR(100) NOT NULL,
    label VARCHAR(150) NOT NULL,
    description VARCHAR(500) NOT NULL,
    avatar_url VARCHAR(1000) NULL,
    provider VARCHAR(100) NOT NULL,
    required_scopes_json JSONB NOT NULL DEFAULT '[]'::jsonb,
    tools_json JSONB NOT NULL DEFAULT '[]'::jsonb,
    is_active BOOLEAN NOT NULL DEFAULT true,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT plugins_plugin_key UNIQUE (plugin_key)
);

INSERT INTO assistant.plugins (
    id,
    plugin_key,
    label,
    description,
    avatar_url,
    provider,
    required_scopes_json,
    tools_json
)
VALUES (
    '7f8f66db-3b7f-4d6b-a18f-e44e301f38b1',
    'google_workspace',
    'Google Workspace',
    'Work across Drive and Calendar',
    'https://www.gstatic.com/images/branding/product/2x/workspace_48dp.png',
    'google',
    '[
        "https://www.googleapis.com/auth/drive.readonly",
        "https://www.googleapis.com/auth/calendar.events"
    ]'::jsonb,
    '[
        {
            "name": "google_drive_search",
            "pluginKey": "google_workspace",
            "label": "Search Google Drive",
            "description": "Search files in the connected Google Drive account.",
            "effect": "read",
            "requiredScopes": ["https://www.googleapis.com/auth/drive.readonly"],
            "parameters": {
                "type": "object",
                "properties": {
                    "query": { "type": "string" },
                    "limit": { "type": "integer" }
                },
                "required": ["query"]
            }
        },
        {
            "name": "google_calendar_list_events",
            "pluginKey": "google_workspace",
            "label": "List Google Calendar events",
            "description": "List events from the connected Google Calendar account.",
            "effect": "read",
            "requiredScopes": ["https://www.googleapis.com/auth/calendar.events"],
            "parameters": {
                "type": "object",
                "properties": {
                    "timeMin": { "type": "string" },
                    "timeMax": { "type": "string" }
                },
                "required": []
            }
        },
        {
            "name": "google_calendar_create_event",
            "pluginKey": "google_workspace",
            "label": "Create Google Calendar event",
            "description": "Create an event in the connected Google Calendar account after user confirmation.",
            "effect": "write",
            "requiredScopes": ["https://www.googleapis.com/auth/calendar.events"],
            "parameters": {
                "type": "object",
                "properties": {
                    "summary": { "type": "string" },
                    "start": { "type": "string" },
                    "end": { "type": "string" },
                    "description": { "type": "string" }
                },
                "required": ["summary", "start", "end"]
            }
        }
    ]'::jsonb
)
ON CONFLICT (plugin_key) DO UPDATE SET
    label = EXCLUDED.label,
    description = EXCLUDED.description,
    avatar_url = EXCLUDED.avatar_url,
    provider = EXCLUDED.provider,
    required_scopes_json = EXCLUDED.required_scopes_json,
    tools_json = EXCLUDED.tools_json,
    is_active = true,
    updated_at = now();

CREATE TABLE IF NOT EXISTS assistant.plugin_installations (
    id UUID PRIMARY KEY,
    user_id UUID NOT NULL,
    plugin_id UUID NOT NULL,
    status VARCHAR(30) NOT NULL,
    config_json JSONB NULL,
    installed_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    disabled_at TIMESTAMPTZ NULL,
    CONSTRAINT plugin_installations_plugin_id_fkey FOREIGN KEY (plugin_id)
        REFERENCES assistant.plugins (id) ON DELETE CASCADE,
    CONSTRAINT plugin_installations_user_plugin_id_key UNIQUE (user_id, plugin_id)
);

CREATE TABLE IF NOT EXISTS assistant.plugin_connections (
    id UUID PRIMARY KEY,
    user_id UUID NOT NULL,
    plugin_id UUID NOT NULL,
    provider_account_id VARCHAR(255) NULL,
    provider_email VARCHAR(320) NULL,
    status VARCHAR(30) NOT NULL,
    scopes_json JSONB NOT NULL DEFAULT '[]'::jsonb,
    encrypted_refresh_token TEXT NULL,
    encrypted_access_token TEXT NULL,
    access_token_expires_at TIMESTAMPTZ NULL,
    token_rotated_at TIMESTAMPTZ NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT plugin_connections_plugin_id_fkey FOREIGN KEY (plugin_id)
        REFERENCES assistant.plugins (id) ON DELETE CASCADE,
    CONSTRAINT plugin_connections_user_plugin_id_key UNIQUE (user_id, plugin_id)
);

CREATE TABLE IF NOT EXISTS assistant.plugin_tool_audits (
    id UUID PRIMARY KEY,
    workspace_id UUID NULL,
    user_id UUID NOT NULL,
    conversation_id UUID NULL,
    assistant_message_id UUID NULL,
    plugin_id UUID NOT NULL,
    plugin_key VARCHAR(100) NOT NULL,
    tool_name VARCHAR(150) NOT NULL,
    input_summary TEXT NULL,
    result_status VARCHAR(80) NOT NULL,
    provider_resource_ref VARCHAR(500) NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT plugin_tool_audits_plugin_id_fkey FOREIGN KEY (plugin_id)
        REFERENCES assistant.plugins (id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS idx_plugin_tool_audits_workspace_created
    ON assistant.plugin_tool_audits (workspace_id, created_at);

CREATE INDEX IF NOT EXISTS idx_plugin_tool_audits_user_created
    ON assistant.plugin_tool_audits (user_id, created_at);
