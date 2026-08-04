UPDATE workspace.workspace_members
SET status = lower(status)
WHERE status IS NOT NULL
  AND status <> lower(status);
