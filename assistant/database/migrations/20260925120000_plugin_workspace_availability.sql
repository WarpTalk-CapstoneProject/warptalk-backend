-- Platform-admin control over which workspaces a marketplace plugin reaches (owner request,
-- 2026-09-25: "cần bật tắt hiển thị ở các workspace").
--
-- THE LAYERS, outermost first. A marketplace plugin is usable in a workspace only if every layer
-- lets it through:
--
--   1. is_active (existing). Retired is retired everywhere; no override brings it back.
--   2. workspace_plugin_overrides (new). A platform admin's decision for ONE workspace: enabled or
--      disabled, with a reason. Beats 3 and 4.
--   3. plugins.allowed_plan_slugs (new). NULL: every plan. A JSON array: only workspaces whose
--      replicated plan is in it; a workspace with no plan never matches.
--   4. plugins.workspace_default (new). 'available': every workspace may have it (the behaviour
--      before this migration, so nothing changes on deploy). 'opt_in': hidden from every workspace
--      that has not been enabled by an override.
--   5. The workspace Owner's own list (workspace_plugins / curation, 20260917100000) - unchanged.
--      The platform decides what an Owner may add; the Owner decides what the workspace has.
--
-- DISABLED MEANS INERT, NOT DELETED. A disabled plugin leaves the Owner's marketplace, cannot be
-- added, requested, installed or connected in that workspace, and its tools are not offered to
-- WarpBot there. Nothing a member holds is touched: installations, connections and tokens stay,
-- so enabling it again restores exactly what was there. The member's page says so.
--
-- Private plugins (owner_workspace_id IS NOT NULL) are not governed here: they belong to one
-- workspace and are not in the marketplace.
--
-- No BEGIN/COMMIT: the migration runner owns the transaction.

ALTER TABLE assistant.plugins
    ADD COLUMN IF NOT EXISTS workspace_default VARCHAR(20) NOT NULL DEFAULT 'available',
    ADD COLUMN IF NOT EXISTS allowed_plan_slugs JSONB NULL;

COMMENT ON COLUMN assistant.plugins.workspace_default IS
    'available: every workspace may have this marketplace plugin unless an override or the plan rule says otherwise. opt_in: hidden from every workspace without an enabling override.';
COMMENT ON COLUMN assistant.plugins.allowed_plan_slugs IS
    'NULL: every plan. A JSON array of plan slugs: only workspaces on one of those plans (the replicated entitlement snapshot). A workspace with no plan never matches. Overrides beat it.';

ALTER TABLE assistant.plugins
    DROP CONSTRAINT IF EXISTS plugins_workspace_default_check;
ALTER TABLE assistant.plugins
    ADD CONSTRAINT plugins_workspace_default_check
    CHECK (workspace_default IN ('available', 'opt_in'));

ALTER TABLE assistant.plugins
    DROP CONSTRAINT IF EXISTS plugins_allowed_plan_slugs_is_array;
ALTER TABLE assistant.plugins
    ADD CONSTRAINT plugins_allowed_plan_slugs_is_array
    CHECK (allowed_plan_slugs IS NULL OR jsonb_typeof(allowed_plan_slugs) = 'array');

CREATE TABLE IF NOT EXISTS assistant.workspace_plugin_overrides (
    id UUID NOT NULL DEFAULT gen_random_uuid(),
    -- No FK: workspaces live in the workspace service's database.
    workspace_id UUID NOT NULL,
    plugin_id UUID NOT NULL,
    state VARCHAR(20) NOT NULL,
    reason VARCHAR(500) NULL,
    -- The platform admin who set it. NULL only if the token carried no usable subject.
    set_by UUID NULL,
    set_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT workspace_plugin_overrides_pkey PRIMARY KEY (id),
    CONSTRAINT workspace_plugin_overrides_workspace_plugin_key UNIQUE (workspace_id, plugin_id),
    CONSTRAINT workspace_plugin_overrides_plugin_id_fkey FOREIGN KEY (plugin_id)
        REFERENCES assistant.plugins (id) ON DELETE CASCADE,
    CONSTRAINT workspace_plugin_overrides_state_check
        CHECK (state IN ('enabled', 'disabled'))
);

-- The UNIQUE above serves the guard's per-workspace read; this one serves the admin's per-plugin
-- "Workspaces" tab and the FK's cascade scan.
CREATE INDEX IF NOT EXISTS idx_workspace_plugin_overrides_plugin_id
    ON assistant.workspace_plugin_overrides (plugin_id);
