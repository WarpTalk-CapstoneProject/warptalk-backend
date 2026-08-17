-- WT-417: retire the membership rows that outlived their workspace.
--
-- Deleting a workspace stamped workspace.deleted_at and left every workspace_members row
-- untouched, removed_at still NULL. Those rows then read as LIVE memberships of a workspace that
-- no longer exists, and three separate rules believed them:
--
--   * IsUserInternalMemberOfAnyEnterpriseWorkspaceAsync — "you may be internal to only one
--     Enterprise workspace" counted the dead one, so the account was permanently barred from
--     joining any Enterprise workspace as Internal, with a 403 naming a workspace it cannot see.
--   * UNIQUE (workspace_id, user_id) has no `WHERE removed_at IS NULL`, so the orphan holds its
--     slot against any future rejoin of that workspace.
--   * Every membership lookup that filters on removed_at IS NULL — which is most of them.
--
-- Nothing un-deletes a workspace (AdminWorkspaceService.ReactivateAsync flips is_active, not
-- deleted_at), so a membership of a deleted workspace has no future in which it becomes valid
-- again. Stamping it loses nothing and is not reversible in any sense the delete was not.
--
-- removed_by is left NULL deliberately: nobody removed these people. The workspace was deleted
-- and this is the backfill catching up, and writing a user id here would name someone who did
-- not perform the act. removed_at is copied from the workspace's own deleted_at rather than set
-- to now(), so the row dates the event that actually caused it — a cleanup run months later must
-- not make it look like these members left today.
UPDATE workspace.workspace_members m
SET removed_at = w.deleted_at
FROM workspace.workspaces w
WHERE m.workspace_id = w.id
  AND w.deleted_at IS NOT NULL
  AND m.removed_at IS NULL;
