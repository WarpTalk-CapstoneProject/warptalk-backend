-- Append-only trail of system-admin lifecycle actions against a workspace (WT-204).
--
-- Suspension state itself stays on workspaces.is_active. The reason lives here so that
-- reactivating a workspace never overwrites why it was suspended, and so the admin audit
-- log (WT-210) has a durable source that predates its own query API.
CREATE TABLE IF NOT EXISTS workspace.workspace_admin_actions (
    id uuid PRIMARY KEY DEFAULT uuidv7(),
    workspace_id uuid NOT NULL REFERENCES workspace.workspaces (id),
    action varchar(30) NOT NULL,
    reason text NOT NULL,
    performed_by uuid NOT NULL,
    performed_at timestamptz NOT NULL DEFAULT now(),
    correlation_id varchar(100),
    CONSTRAINT workspace_admin_actions_action_check
        CHECK (action IN ('suspend', 'reactivate'))
);

CREATE INDEX IF NOT EXISTS idx_workspace_admin_actions_workspace
    ON workspace.workspace_admin_actions (workspace_id, performed_at DESC);

CREATE INDEX IF NOT EXISTS idx_workspace_admin_actions_performed_at
    ON workspace.workspace_admin_actions (performed_at DESC);

-- Deliberately no UPDATE or DELETE grant: the runtime role can append and read the trail
-- but cannot rewrite it.
GRANT SELECT, INSERT
    ON workspace.workspace_admin_actions
    TO warptalk_workspace_runtime;

COMMENT ON TABLE workspace.workspace_admin_actions IS
    'Append-only audit trail of system-admin workspace lifecycle actions (WT-204).';
