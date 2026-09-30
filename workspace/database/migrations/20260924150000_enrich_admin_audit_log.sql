-- The platform audit screen (/admin/audit) shows who, from where, to what and why — not just a
-- verb and a GUID. These columns hold the parts only the service the admin called can know.
--
-- Expand-only and nullable: every row written before this has none of them, and the reader
-- resolves what it can (the actor's name, a workspace's name) at read time instead of guessing
-- here. The table stays append-only — no UPDATE or DELETE grant is added, and the SELECT/INSERT
-- grant on the table already covers new columns.
--
-- No transaction control: the migration runner owns the transaction.

ALTER TABLE workspace.workspace_admin_actions
    ADD COLUMN IF NOT EXISTS actor_email varchar(320),
    ADD COLUMN IF NOT EXISTS actor_name varchar(200),
    ADD COLUMN IF NOT EXISTS entity_key varchar(100),
    ADD COLUMN IF NOT EXISTS entity_label varchar(200),
    ADD COLUMN IF NOT EXISTS error_message text,
    ADD COLUMN IF NOT EXISTS ip_address varchar(64),
    ADD COLUMN IF NOT EXISTS user_agent varchar(512);

-- Keyset pagination walks (performed_at, id) downwards; the existing performed_at index alone
-- leaves the tie-break to a sort.
CREATE INDEX IF NOT EXISTS idx_workspace_admin_actions_performed_at_id
    ON workspace.workspace_admin_actions (performed_at DESC, id DESC);

-- A subject addressed by its natural key (a language code, a plugin key) is looked up by it.
CREATE INDEX IF NOT EXISTS idx_workspace_admin_actions_entity_key
    ON workspace.workspace_admin_actions (entity_type, entity_key, performed_at DESC)
    WHERE entity_key IS NOT NULL;

COMMENT ON COLUMN workspace.workspace_admin_actions.entity_key IS
    'Natural key of a subject that has no GUID (language code, plugin key). entity_id stays NULL for those.';
COMMENT ON COLUMN workspace.workspace_admin_actions.error_message IS
    'Why a failed entry failed, in the words the caller was given. NULL on success.';
COMMENT ON COLUMN workspace.workspace_admin_actions.ip_address IS
    'The admin''s address as the gateway forwarded it (first X-Forwarded-For hop), never a service''s.';
