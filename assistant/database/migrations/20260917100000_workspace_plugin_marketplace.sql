-- Three-tier plugin marketplace (owner decision, 2026-09-17).
--
--   * A system admin curates the MARKETPLACE: plugins rows with owner_workspace_id IS NULL.
--   * A workspace Owner decides which marketplace plugins their workspace has (workspace_plugins),
--     and may add a PRIVATE MCP plugin that only their workspace sees (plugins.owner_workspace_id).
--   * A member asks the Owner for a plugin the workspace does not have (plugin_requests).
--
-- A plugin is usable in a workspace iff it is in workspace_plugins for that workspace, or it is a
-- private plugin owned by that workspace.
--
-- TRANSITION: NOBODY LOSES A PLUGIN ON DEPLOY
--   Before this, a workspace had one switch - the workspace service's AllowAnyPlugins, default
--   true - and every active catalog row was usable in every workspace that left it on. The list of
--   workspaces lives in the workspace service's own database, so this migration cannot backfill
--   workspace_plugins for them: there is nothing in this database to join against.
--
--   So the transition is lazy, and workspace_plugin_curations is what marks it. A workspace with no
--   curation row is still judged by AllowAnyPlugins exactly as before (true: every marketplace
--   plugin is available; false: none is). The first time its Owner changes the list - adding,
--   removing, or approving a request - the service writes the curation row and, if the workspace
--   had AllowAnyPlugins on, seeds workspace_plugins with every active marketplace plugin in the same
--   transaction as the change. From then on only the list counts. A member therefore keeps every
--   plugin they could use the day before, until an Owner deliberately removes one.
--
--   The curation row is needed on its own, rather than "has any workspace_plugins rows", because an
--   Owner who removes every plugin must not fall back to "all plugins allowed".
--
-- No BEGIN/COMMIT: the migration runner owns the transaction.

ALTER TABLE assistant.plugins
    ADD COLUMN IF NOT EXISTS owner_workspace_id UUID NULL,
    ADD COLUMN IF NOT EXISTS created_by UUID NULL;

COMMENT ON COLUMN assistant.plugins.owner_workspace_id IS
    'NULL: a marketplace plugin curated by a system admin. Set: a private MCP plugin created by that workspace''s Owner and visible only inside it. No FK - workspaces live in the workspace service''s database.';
COMMENT ON COLUMN assistant.plugins.created_by IS
    'Who created the row. NULL for rows written by a migration or a seed.';

-- A private plugin is always a remote MCP server; a native provider is compiled in and global.
ALTER TABLE assistant.plugins
    DROP CONSTRAINT IF EXISTS plugins_private_is_mcp;
ALTER TABLE assistant.plugins
    ADD CONSTRAINT plugins_private_is_mcp
    CHECK (owner_workspace_id IS NULL OR kind = 'mcp');

CREATE INDEX IF NOT EXISTS idx_plugins_owner_workspace_id
    ON assistant.plugins (owner_workspace_id)
    WHERE owner_workspace_id IS NOT NULL;

CREATE TABLE IF NOT EXISTS assistant.workspace_plugins (
    id UUID NOT NULL DEFAULT gen_random_uuid(),
    workspace_id UUID NOT NULL,
    plugin_id UUID NOT NULL,
    -- NULL when the row was seeded by the transition rather than added by a person.
    added_by UUID NULL,
    added_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT workspace_plugins_pkey PRIMARY KEY (id),
    CONSTRAINT workspace_plugins_workspace_plugin_key UNIQUE (workspace_id, plugin_id),
    CONSTRAINT workspace_plugins_plugin_id_fkey FOREIGN KEY (plugin_id)
        REFERENCES assistant.plugins (id) ON DELETE CASCADE
);

-- The UNIQUE above serves lookups by workspace; this one serves the admin "workspaces using" count
-- and the FK's cascade scan.
CREATE INDEX IF NOT EXISTS idx_workspace_plugins_plugin_id
    ON assistant.workspace_plugins (plugin_id);

CREATE TABLE IF NOT EXISTS assistant.workspace_plugin_curations (
    workspace_id UUID NOT NULL,
    curated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    curated_by UUID NULL,
    -- What AllowAnyPlugins said at the moment the list was first written, kept so the transition
    -- can be explained after the fact.
    seeded_from_allow_any_plugins BOOLEAN NOT NULL DEFAULT false,
    CONSTRAINT workspace_plugin_curations_pkey PRIMARY KEY (workspace_id)
);

CREATE TABLE IF NOT EXISTS assistant.plugin_requests (
    id UUID NOT NULL DEFAULT gen_random_uuid(),
    workspace_id UUID NOT NULL,
    plugin_id UUID NOT NULL,
    requested_by UUID NOT NULL,
    reason VARCHAR(500) NULL,
    status VARCHAR(20) NOT NULL DEFAULT 'pending',
    decided_by UUID NULL,
    decided_at TIMESTAMPTZ NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT plugin_requests_pkey PRIMARY KEY (id),
    CONSTRAINT plugin_requests_plugin_id_fkey FOREIGN KEY (plugin_id)
        REFERENCES assistant.plugins (id) ON DELETE CASCADE,
    CONSTRAINT plugin_requests_status_check
        CHECK (status IN ('pending', 'approved', 'declined')),
    -- A decided request says who decided it and when; a pending one says neither.
    CONSTRAINT plugin_requests_decision_check
        CHECK ((status = 'pending') = (decided_at IS NULL))
);

-- At most one PENDING request per (workspace, plugin, requester). Decided requests do not count, so
-- a member whose request was declined can ask again later.
CREATE UNIQUE INDEX IF NOT EXISTS plugin_requests_one_pending
    ON assistant.plugin_requests (workspace_id, plugin_id, requested_by)
    WHERE status = 'pending';

CREATE INDEX IF NOT EXISTS idx_plugin_requests_workspace_status_created
    ON assistant.plugin_requests (workspace_id, status, created_at);
