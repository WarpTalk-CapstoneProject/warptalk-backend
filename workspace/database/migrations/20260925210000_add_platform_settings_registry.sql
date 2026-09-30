-- Migration: 20260925210000_add_platform_settings_registry
-- Ticket: Platform settings console (/admin/settings)
-- Created At: 2026-09-25
-- Description:
--   The platform settings registry is code (WarpTalk.Shared.PlatformSettings.PlatformSettingsCatalog):
--   every key, its type, bounds, default and the service that reads it. The database only holds the
--   values an operator chose, and the history of every change.
--
--   1. workspace.platform_setting_values  — one row per (setting, scope). scope_type is platform
--      (scope_id ''), plan (scope_id = plan slug) or workspace (scope_id = workspace id). No row means
--      "not set": the reading service keeps its deploy-time configuration. version is bumped on every
--      write, and a write must name the version it read.
--   2. workspace.platform_setting_changes — append-only history: who, when, why, old -> new. A revert
--      is a new row pointing at the change it undid. The runtime role may only SELECT and INSERT.
--
--   The workspace service publishes the stored values to Redis (platform:settings:v1:*), where every
--   owning service reads them on a short cache. Every change is also written to the admin audit log
--   (entity type platform_setting) in the same save.
--   Idempotent (IF NOT EXISTS). No BEGIN/COMMIT: the migration runner owns the transaction.

CREATE TABLE IF NOT EXISTS workspace.platform_setting_values (
    setting_key varchar(120) NOT NULL,
    scope_type  varchar(20)  NOT NULL,
    scope_id    varchar(80)  NOT NULL DEFAULT '',
    value       jsonb        NOT NULL,
    version     integer      NOT NULL DEFAULT 1,
    created_at  timestamptz  NOT NULL DEFAULT now(),
    updated_at  timestamptz  NOT NULL DEFAULT now(),
    updated_by  uuid         NULL,
    CONSTRAINT platform_setting_values_pkey PRIMARY KEY (setting_key, scope_type, scope_id),
    CONSTRAINT platform_setting_values_scope_type
        CHECK (scope_type IN ('platform', 'plan', 'workspace')),
    CONSTRAINT platform_setting_values_scope_id
        CHECK ((scope_type = 'platform') = (scope_id = '')),
    CONSTRAINT platform_setting_values_version CHECK (version >= 1)
);

COMMENT ON TABLE workspace.platform_setting_values IS
    'Values operators set on registered platform settings (registry in WarpTalk.Shared.PlatformSettings). No row = not set; the owning service keeps its deploy-time value.';

CREATE TABLE IF NOT EXISTS workspace.platform_setting_changes (
    id               uuid         PRIMARY KEY DEFAULT uuidv7(),
    setting_key      varchar(120) NOT NULL,
    scope_type       varchar(20)  NOT NULL,
    scope_id         varchar(80)  NOT NULL DEFAULT '',
    action           varchar(20)  NOT NULL,
    old_value        jsonb        NULL,
    new_value        jsonb        NULL,
    version          integer      NOT NULL DEFAULT 0,
    reason           text         NULL,
    changed_by       uuid         NOT NULL,
    changed_by_email varchar(320) NULL,
    changed_by_name  varchar(200) NULL,
    changed_at       timestamptz  NOT NULL DEFAULT now(),
    correlation_id   varchar(100) NULL,
    revert_of        uuid         NULL,
    CONSTRAINT platform_setting_changes_action
        CHECK (action IN ('set', 'reset', 'revert', 'import')),
    CONSTRAINT platform_setting_changes_scope_type
        CHECK (scope_type IN ('platform', 'plan', 'workspace')),
    CONSTRAINT platform_setting_changes_reason_length
        CHECK (reason IS NULL OR char_length(reason) <= 1000)
);

CREATE INDEX IF NOT EXISTS idx_platform_setting_changes_key
    ON workspace.platform_setting_changes (setting_key, changed_at DESC);

CREATE INDEX IF NOT EXISTS idx_platform_setting_changes_changed_at
    ON workspace.platform_setting_changes (changed_at DESC);

COMMENT ON TABLE workspace.platform_setting_changes IS
    'Append-only history of platform setting changes: who, when, why, old -> new. Reverts are new rows.';

GRANT SELECT, INSERT, UPDATE, DELETE
    ON workspace.platform_setting_values
    TO warptalk_workspace_runtime;

-- Append-only, as admin_inbox_notes and workspace_admin_notes.
GRANT SELECT, INSERT
    ON workspace.platform_setting_changes
    TO warptalk_workspace_runtime;
