-- A workspace Owner's rule for one tool of one plugin in that workspace (owner request,
-- 2026-10-01): ask every time ('approval') or never ('blocked').
--
-- STRICTER WINS. Each member already chooses allow / approval / blocked per tool for their own
-- account (installation config_json -> toolPolicy, WT-687). WarpBot gets the stricter of the
-- member's choice and this rule, so a rule can only tighten. There is deliberately no 'allow':
-- it would overrule a member who blocked a tool on their own account.
--
-- ONLY RULES ARE STORED. No row is "member's choice", which is also what every workspace has
-- after this migration, so nothing changes on deploy.
--
-- Its own table, not a column on workspace_plugins: a workspace whose plugin list was never
-- curated has no rows there, and its Owner can still need to block a tool.
--
-- No BEGIN/COMMIT: the migration runner owns the transaction.

CREATE TABLE IF NOT EXISTS assistant.workspace_plugin_tool_policies (
    id UUID NOT NULL DEFAULT gen_random_uuid(),
    -- No FK: workspaces live in the workspace service's database.
    workspace_id UUID NOT NULL,
    plugin_id UUID NOT NULL,
    -- As the plugin's manifest declares it (PluginToolManifestValidator: 1-150 characters). A rule
    -- for a tool the manifest later drops is kept and simply matches nothing.
    tool_name VARCHAR(150) NOT NULL,
    policy VARCHAR(20) NOT NULL,
    -- The Owner who set it.
    set_by UUID NOT NULL,
    set_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT workspace_plugin_tool_policies_pkey PRIMARY KEY (id),
    CONSTRAINT workspace_plugin_tool_policies_workspace_plugin_tool_key
        UNIQUE (workspace_id, plugin_id, tool_name),
    CONSTRAINT workspace_plugin_tool_policies_plugin_id_fkey FOREIGN KEY (plugin_id)
        REFERENCES assistant.plugins (id) ON DELETE CASCADE,
    CONSTRAINT workspace_plugin_tool_policies_policy_check
        CHECK (policy IN ('approval', 'blocked'))
);

-- The UNIQUE above serves the per-workspace read on every WarpBot turn; this one serves the FK's
-- cascade scan when a plugin is deleted from the catalog.
CREATE INDEX IF NOT EXISTS idx_workspace_plugin_tool_policies_plugin_id
    ON assistant.workspace_plugin_tool_policies (plugin_id);

COMMENT ON TABLE assistant.workspace_plugin_tool_policies IS
    'A workspace Owner''s rule for one plugin tool: approval (ask every time) or blocked. No row: member''s choice. WarpBot gets the stricter of this and the member''s own tool policy.';
