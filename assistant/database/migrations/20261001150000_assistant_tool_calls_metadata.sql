-- Every WarpBot tool call, recorded as metadata only (wave 4, 2026-10-01): built-in, web search and
-- plugin. Insights -> Tools (workspace) and the admin WarpBot tools page read it.
--
-- WHY THIS TABLE. assistant_tool_calls has existed since the assistant schema was created and
-- nothing ever wrote to it. Until now the only count was plugin_tool_audits, which sees plugin
-- calls and nothing else: built-in calls lived only inside assistant_messages.tool_results_json and
-- web search calls were not stored at all.
--
-- PRIVACY. No argument or result text is ever stored here, exactly as plugin_tool_audits keeps
-- none. arguments_json stays NOT NULL and is always written as ''; result_json is always NULL. The
-- text the UI shows lives in assistant_messages.tool_results_json, where it already was.
--
-- WHO. workspace_id and user_id are the conversation's, denormalised so a workspace's usage is one
-- index range and not a join through assistant_messages. Nullable because rows written before this
-- migration (there are none in practice) carry neither.
--
-- OLD ROWS. The NOT NULL columns have defaults, so any existing row reads as a successful built-in
-- call. Nothing is backfilled: calls made before this release were never recorded, and the
-- endpoints report the earliest row as "recording since".
--
-- No BEGIN/COMMIT: the migration runner owns the transaction.

ALTER TABLE assistant.assistant_tool_calls
    -- No FK: workspaces live in the workspace service's database.
    ADD COLUMN IF NOT EXISTS workspace_id UUID NULL,
    -- No FK: users live in the auth service's database.
    ADD COLUMN IF NOT EXISTS user_id UUID NULL,
    -- 'builtin' | 'plugin' | 'web_search'
    ADD COLUMN IF NOT EXISTS source VARCHAR(20) NOT NULL DEFAULT 'builtin',
    -- The plugin the tool belongs to when source = 'plugin'; NULL otherwise.
    ADD COLUMN IF NOT EXISTS plugin_key VARCHAR(100) NULL,
    -- 'ok' | 'error' | 'blocked' | 'needs_setup' | 'declined' | 'confirmation_required'
    ADD COLUMN IF NOT EXISTS outcome VARCHAR(30) NOT NULL DEFAULT 'ok',
    -- The plugin error code (PluginConstants.ErrorCodes) or a built-in handler's error status.
    ADD COLUMN IF NOT EXISTS outcome_code VARCHAR(60) NULL,
    -- Wall time around the tool execution. NULL when not measurable (web search).
    ADD COLUMN IF NOT EXISTS duration_ms INTEGER NULL;

-- One workspace's usage over a window.
CREATE INDEX IF NOT EXISTS idx_assistant_tool_calls_workspace_created
    ON assistant.assistant_tool_calls (workspace_id, created_at);

-- The platform-wide window, and the global "recording since" (MIN(created_at)).
CREATE INDEX IF NOT EXISTS idx_assistant_tool_calls_created
    ON assistant.assistant_tool_calls (created_at);

-- One tool's history.
CREATE INDEX IF NOT EXISTS idx_assistant_tool_calls_tool_created
    ON assistant.assistant_tool_calls (tool_name, created_at);

-- A re-finalised answer deletes its message's rows before writing them again, and the FK's
-- cascade scans by message_id when a conversation is deleted. Neither had an index.
CREATE INDEX IF NOT EXISTS idx_assistant_tool_calls_message_id
    ON assistant.assistant_tool_calls (message_id);

COMMENT ON TABLE assistant.assistant_tool_calls IS
    'One row per WarpBot tool call (builtin, plugin, web_search): metadata only. arguments_json is always '''' and result_json always NULL - no argument or result text is stored.';
