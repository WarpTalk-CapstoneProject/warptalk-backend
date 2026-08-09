-- Join-request tracking columns, which the workspace service's own database never received.
--
-- THE FAILURE THIS FIXES
--   Every listing of join requests 500s in production with
--   `column w.requested_by does not exist`. It has been doing so quietly — the Invitations page
--   swallowed the error and rendered an empty "Join Requests 0" tab, so the workspace looked
--   like it had no pending requests rather than like it could not read them. Members now says
--   so out loud, which is how this surfaced.
--
-- WHY THE COLUMN IS MISSING
--   requested_by / reviewed_by / reviewed_at were added by
--   scripts/migrations/038-28-07-2026-add-workspace-join-request-tracking.sql, which applies to
--   the legacy monolith database `warptalk`. No service has connected to that database since
--   the logical-database extraction; WorkspaceService reads warptalk_workspace, which never got
--   these columns. The migration reported success and changed nothing the service can see.
--
--   This is the WT-294 failure exactly, and check-service-migration-coverage.sh exists to catch
--   it — but its mirroring rule only applies to legacy migrations numbered >= 49, on the
--   reasoning that anything below that predates the extraction and came across in the dump.
--   038 is below the threshold and did not come across, so it fell through the gap.
--
-- SAFE TO RE-RUN
--   Every statement is idempotent, and the backfill only fills NULLs, so re-applying it cannot
--   overwrite a reviewer decision recorded later.

ALTER TABLE workspace.workspace_invitations
  ADD COLUMN IF NOT EXISTS requested_by uuid,
  ADD COLUMN IF NOT EXISTS reviewed_by uuid,
  ADD COLUMN IF NOT EXISTS reviewed_at timestamptz;

-- External AuthService user ids. No physical FK on purpose: the users live in another service's
-- database, and 044 dropped exactly these constraints from the legacy copy for that reason.
COMMENT ON COLUMN workspace.workspace_invitations.requested_by IS 'External AuthService user id. No physical FK.';
COMMENT ON COLUMN workspace.workspace_invitations.reviewed_by IS 'External AuthService user id. No physical FK.';

-- A request created before this migration recorded its author in invited_by, because that was
-- the only column available. Without this, every pre-existing request lists no requester.
UPDATE workspace.workspace_invitations
SET requested_by = invited_by
WHERE status = 'REQUESTED'
  AND requested_by IS NULL;

CREATE INDEX IF NOT EXISTS ix_workspace_invitations_workspace_id_status_created_at
  ON workspace.workspace_invitations (workspace_id, status, created_at DESC);

CREATE INDEX IF NOT EXISTS ix_workspace_invitations_requested_by_status_created_at
  ON workspace.workspace_invitations (requested_by, status, created_at DESC)
  WHERE requested_by IS NOT NULL;
