-- Keep the legacy index in place: existing installations may have created it
-- under the bootstrap superuser, while service migrations run as the stable
-- warptalk_migrator role and cannot safely drop objects owned by another role.
CREATE INDEX IF NOT EXISTS idx_workspace_outbox_pending_dispatch_v2
    ON workspace.outbox_messages (available_at, created_at)
    WHERE published_at IS NULL AND dead_lettered_at IS NULL;
