CREATE INDEX IF NOT EXISTS idx_outbox_messages_pending_dispatch_v2
    ON subscription.outbox_messages (available_at, created_at)
    WHERE published_at IS NULL AND dead_lettered_at IS NULL;
