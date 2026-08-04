-- Generalize the workspace lifecycle trail into the platform-wide admin audit log (WT-210).
--
-- WT-204 introduced workspace.workspace_admin_actions for suspend/reactivate only. The admin
-- portal needs one attributable record for every administrative mutation — pricing, payment
-- methods, credit adjustments, glossary, notifications — and those live in other services with
-- their own logical databases. Rather than let services write across database boundaries, they
-- publish admin.action_recorded and the workspace service appends here.
--
-- Expand-only: existing columns keep their meaning, new columns carry defaults, and no data is
-- rewritten. The table stays append-only (still no UPDATE/DELETE grant).

ALTER TABLE workspace.workspace_admin_actions
    ADD COLUMN IF NOT EXISTS source_service varchar(50) NOT NULL DEFAULT 'workspace-service',
    ADD COLUMN IF NOT EXISTS entity_type varchar(50) NOT NULL DEFAULT 'workspace',
    ADD COLUMN IF NOT EXISTS entity_id uuid,
    ADD COLUMN IF NOT EXISTS result varchar(20) NOT NULL DEFAULT 'succeeded',
    ADD COLUMN IF NOT EXISTS before_summary jsonb,
    ADD COLUMN IF NOT EXISTS after_summary jsonb;

-- Actions from other services are not workspace-scoped (a pricing change is platform-wide),
-- so workspace_id becomes optional and entity_id carries the subject instead.
ALTER TABLE workspace.workspace_admin_actions
    ALTER COLUMN workspace_id DROP NOT NULL;

-- The original CHECK only allowed suspend/reactivate. Actions are now open-ended per source
-- service; the API validates them, and a rigid list here would reject every future action.
ALTER TABLE workspace.workspace_admin_actions
    DROP CONSTRAINT IF EXISTS workspace_admin_actions_action_check;

ALTER TABLE workspace.workspace_admin_actions
    ADD CONSTRAINT workspace_admin_actions_result_check
        CHECK (result IN ('succeeded', 'failed'));

-- Backfill the generic subject for the rows WT-204 already wrote.
UPDATE workspace.workspace_admin_actions
SET entity_id = workspace_id
WHERE entity_id IS NULL
  AND workspace_id IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_workspace_admin_actions_entity
    ON workspace.workspace_admin_actions (entity_type, entity_id, performed_at DESC);

CREATE INDEX IF NOT EXISTS idx_workspace_admin_actions_actor
    ON workspace.workspace_admin_actions (performed_by, performed_at DESC);

CREATE INDEX IF NOT EXISTS idx_workspace_admin_actions_action
    ON workspace.workspace_admin_actions (action, performed_at DESC);

-- Deduplicates replayed admin.action_recorded messages: the same correlation id from the same
-- service for the same action is one event, however many times the bus delivers it.
CREATE UNIQUE INDEX IF NOT EXISTS uq_workspace_admin_actions_correlation
    ON workspace.workspace_admin_actions (source_service, correlation_id, action, entity_id)
    WHERE correlation_id IS NOT NULL;

COMMENT ON TABLE workspace.workspace_admin_actions IS
    'Append-only platform admin audit log (WT-204, generalized by WT-210). Written directly by '
    'workspace lifecycle actions and by admin.action_recorded events from other services.';
