CREATE TABLE IF NOT EXISTS assistant.plugin_confirmation_tokens (
    id UUID PRIMARY KEY,
    user_id UUID NOT NULL,
    workspace_id UUID NULL,
    plugin_id UUID NOT NULL,
    plugin_key VARCHAR(100) NOT NULL,
    tool_name VARCHAR(150) NOT NULL,
    argument_hash VARCHAR(64) NOT NULL,
    expires_at TIMESTAMPTZ NOT NULL,
    consumed_at TIMESTAMPTZ NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT plugin_confirmation_tokens_plugin_id_fkey FOREIGN KEY (plugin_id)
        REFERENCES assistant.plugins (id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS idx_plugin_confirmation_tokens_user_expires
    ON assistant.plugin_confirmation_tokens (user_id, expires_at);

CREATE INDEX IF NOT EXISTS idx_plugin_confirmation_tokens_plugin_tool_created
    ON assistant.plugin_confirmation_tokens (plugin_id, tool_name, created_at);
