-- Migration: 20260924100000_email_template_cms_and_announcements
-- Created At: 2026-09-24
-- Description:
--   The two admin content-management surfaces: editable transactional email templates, and
--   announcements shown inside the app.
--
--   EMAIL TEMPLATES
--     notification.notification_templates existed with an entity and a repository and nothing
--     reading it. It now holds the admin-edited version of each transactional email
--     (channel = 'EMAIL', type = the EmailTemplateCatalog key, e.g. 'auth.verify-email'). Every
--     sender reads it at send time — directly here, and over the GetEmailTemplate RPC from auth,
--     workspace and translation-room — and falls back to the built-in wording when there is no
--     active row. Added columns:
--       heading      the email's title line
--       version      bumped on every save, restore and reset; matches the newest history row
--       created_by / updated_by   the admin (external AuthService id, no physical FK)
--     CREATE TABLE IF NOT EXISTS keeps this safe on a database extracted without the table.
--
--     notification.notification_template_versions is the append-only history: one row per save,
--     restore (restored_from_version set) and reset (content = the default at the time).
--
--   ANNOUNCEMENTS
--     notification.announcements: CMS content. status stores DRAFT / PUBLISHED / ARCHIVED only;
--     "scheduled" and "ended" are a published row read against starts_at / ends_at, so no worker
--     has to flip anything for a schedule to take effect. Audience is ALL, PLANS
--     (audience_plan_slugs) or WORKSPACES (audience_workspace_ids).
--
--     notification.announcement_dismissals: who closed which announcement. Deleted with it.
--
--   Additive only; nothing existing is rewritten. No explicit transaction control: the migration
--   runner owns the transaction.

CREATE TABLE IF NOT EXISTS notification.notification_templates (
    id uuid PRIMARY KEY DEFAULT (uuidv7()),
    type varchar(50) NOT NULL,
    channel varchar(20) NOT NULL,
    subject varchar(255),
    body_template text NOT NULL,
    variables jsonb NOT NULL DEFAULT '[]'::jsonb,
    is_active boolean NOT NULL DEFAULT true,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now()
);

ALTER TABLE notification.notification_templates
    ADD COLUMN IF NOT EXISTS heading varchar(255) NULL,
    ADD COLUMN IF NOT EXISTS version integer NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS created_by uuid NULL,
    ADD COLUMN IF NOT EXISTS updated_by uuid NULL;

-- One row per email per channel. The full schema declared this as an unnamed unique index; this
-- names it so the check does not depend on which of the two historical shapes a database has.
CREATE UNIQUE INDEX IF NOT EXISTS uq_notification_templates_type_channel
    ON notification.notification_templates (type, channel);

COMMENT ON COLUMN notification.notification_templates.heading IS 'Email title line. EMAIL channel only.';
COMMENT ON COLUMN notification.notification_templates.version IS 'Bumped on every save, restore and reset; equals the newest notification_template_versions.version.';

CREATE TABLE IF NOT EXISTS notification.notification_template_versions (
    id uuid PRIMARY KEY,
    template_type varchar(50) NOT NULL,
    channel varchar(20) NOT NULL,
    version integer NOT NULL,
    action varchar(20) NOT NULL,
    restored_from_version integer NULL,
    subject varchar(255) NOT NULL,
    heading varchar(255) NOT NULL DEFAULT '',
    body_template text NOT NULL,
    note varchar(500) NULL,
    created_by uuid NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ck_notification_template_versions_action CHECK (action IN ('SAVED', 'RESTORED', 'RESET')),
    CONSTRAINT uq_notification_template_versions_type_channel_version UNIQUE (template_type, channel, version)
);

COMMENT ON TABLE notification.notification_template_versions IS 'Append-only history of admin edits to notification_templates.';
COMMENT ON COLUMN notification.notification_template_versions.created_by IS 'External AuthService user id. No physical FK.';

CREATE TABLE IF NOT EXISTS notification.announcements (
    id uuid PRIMARY KEY,
    title varchar(200) NOT NULL,
    body_markdown text NOT NULL,
    type varchar(30) NOT NULL,
    status varchar(20) NOT NULL,
    audience_mode varchar(20) NOT NULL DEFAULT 'ALL',
    audience_plan_slugs text[] NOT NULL DEFAULT '{}',
    audience_workspace_ids uuid[] NOT NULL DEFAULT '{}',
    cta_label varchar(60) NULL,
    cta_url varchar(2048) NULL,
    starts_at timestamptz NULL,
    ends_at timestamptz NULL,
    published_at timestamptz NULL,
    published_by uuid NULL,
    archived_at timestamptz NULL,
    created_by uuid NOT NULL,
    updated_by uuid NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ck_announcements_status CHECK (status IN ('DRAFT', 'PUBLISHED', 'ARCHIVED')),
    CONSTRAINT ck_announcements_type CHECK (type IN ('ANNOUNCEMENT', 'FEATURE', 'MAINTENANCE', 'PROMOTION')),
    CONSTRAINT ck_announcements_audience_mode CHECK (audience_mode IN ('ALL', 'PLANS', 'WORKSPACES')),
    CONSTRAINT ck_announcements_window CHECK (starts_at IS NULL OR ends_at IS NULL OR ends_at > starts_at)
);

CREATE INDEX IF NOT EXISTS idx_announcements_status_window
    ON notification.announcements (status, starts_at, ends_at);
CREATE INDEX IF NOT EXISTS idx_announcements_created_at
    ON notification.announcements (created_at DESC);

COMMENT ON TABLE notification.announcements IS 'In-app announcements managed from the admin portal. Scheduled/ended are derived from status + window.';
COMMENT ON COLUMN notification.announcements.created_by IS 'External AuthService user id. No physical FK.';
COMMENT ON COLUMN notification.announcements.audience_workspace_ids IS 'External WorkspaceService ids. No physical FK.';

CREATE TABLE IF NOT EXISTS notification.announcement_dismissals (
    announcement_id uuid NOT NULL REFERENCES notification.announcements (id) ON DELETE CASCADE,
    user_id uuid NOT NULL,
    dismissed_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (announcement_id, user_id)
);

CREATE INDEX IF NOT EXISTS idx_announcement_dismissals_user
    ON notification.announcement_dismissals (user_id);

COMMENT ON COLUMN notification.announcement_dismissals.user_id IS 'External AuthService user id. No physical FK.';
