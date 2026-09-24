-- Internal notes a platform administrator leaves on a workspace, from the admin workspace page.
--
-- Platform-side only: never read by any tenant-facing endpoint. Append-only like
-- workspace_admin_actions beside it — a correction is a new note, so what was known when stays
-- readable. Adding a note is also recorded in the admin audit log (action note.added).
--
-- No BEGIN/COMMIT: the migration runner owns the transaction.
CREATE TABLE IF NOT EXISTS workspace.workspace_admin_notes (
    id uuid PRIMARY KEY DEFAULT uuidv7(),
    workspace_id uuid NOT NULL REFERENCES workspace.workspaces (id),
    body text NOT NULL,
    author_id uuid NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT workspace_admin_notes_body_length
        CHECK (char_length(btrim(body)) BETWEEN 1 AND 4000)
);

CREATE INDEX IF NOT EXISTS idx_workspace_admin_notes_workspace
    ON workspace.workspace_admin_notes (workspace_id, created_at DESC);

-- Deliberately no UPDATE or DELETE grant, the same as the audit log.
GRANT SELECT, INSERT
    ON workspace.workspace_admin_notes
    TO warptalk_workspace_runtime;

COMMENT ON TABLE workspace.workspace_admin_notes IS
    'Append-only internal admin notes on a workspace (admin workspace page). Never tenant-visible.';
