CREATE TABLE IF NOT EXISTS workspace.outbox_messages (
    id uuid PRIMARY KEY,
    event_type varchar(150) NOT NULL,
    compatibility_event_type varchar(100) NOT NULL,
    schema_version integer NOT NULL DEFAULT 1,
    occurred_at timestamptz NOT NULL,
    producer varchar(100) NOT NULL,
    correlation_id varchar(100),
    causation_id varchar(100),
    workspace_id uuid,
    payload_json jsonb NOT NULL,
    attempt_count integer NOT NULL DEFAULT 0,
    available_at timestamptz NOT NULL,
    published_at timestamptz,
    locked_at timestamptz,
    dead_lettered_at timestamptz,
    last_error text,
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_workspace_outbox_dispatch
    ON workspace.outbox_messages (published_at, available_at, created_at);

CREATE INDEX IF NOT EXISTS idx_workspace_outbox_dead_letter
    ON workspace.outbox_messages (dead_lettered_at, created_at)
    WHERE dead_lettered_at IS NOT NULL;

GRANT SELECT, INSERT, UPDATE, DELETE
    ON workspace.outbox_messages
    TO warptalk_workspace_runtime;

COMMENT ON TABLE workspace.outbox_messages IS
    'Transactional outbox for durable Workspace domain-event delivery.';
