-- Migration: 20260926100000_custom_email_templates_and_campaigns
-- Ticket: Email CMS v3 — custom templates, audience sends, thumbnails and a real preview
-- Created At: 2026-09-26
-- Description:
--   Until now every email was a code-defined catalog entry (WarpTalk.Shared.Email.EmailTemplateCatalog)
--   bound to the service that sends it. Admins can now create their own templates. A custom
--   template has no code sender, so it is sent through one of two routes the notification
--   service owns:
--     * an audience send ("campaign") that the admin confirms, and
--     * the email channel of an announcement, sent to the announcement's audience when it goes live.
--
--   email_custom_templates      the definition of a custom template: key, name, category, and its
--                               typed variables with sample values. Its content lives in
--                               email_content_variants, exactly like a built-in's, so drafts,
--                               publishing, versions and the preview work the same way.
--                               Deleting is a soft delete (status DELETED) with a reason, and can be
--                               undone; a template that never sent anything can be removed for good.
--   email_campaigns             one audience send: template, audience, values, schedule, progress.
--   email_campaign_recipients   who the send resolved to and what happened to each of them.
--   announcements               email_template_key / email_campaign_id: the optional email channel.
--
--   Idempotent (IF NOT EXISTS), no BEGIN/COMMIT — the migration runner owns the transaction.

CREATE TABLE IF NOT EXISTS notification.email_custom_templates (
    id uuid PRIMARY KEY,
    key varchar(60) NOT NULL,
    name varchar(120) NOT NULL,
    description varchar(500),
    category varchar(30) NOT NULL,
    variables jsonb NOT NULL DEFAULT '[]'::jsonb,
    status varchar(20) NOT NULL DEFAULT 'ACTIVE',
    deleted_at timestamptz,
    deleted_by uuid,
    delete_reason varchar(500),
    created_by uuid NOT NULL,
    created_at timestamptz NOT NULL DEFAULT NOW(),
    updated_by uuid,
    updated_at timestamptz NOT NULL DEFAULT NOW(),
    CONSTRAINT uq_email_custom_templates_key UNIQUE (key),
    CONSTRAINT ck_email_custom_templates_category CHECK (category IN ('TRANSACTIONAL_CUSTOM', 'MARKETING', 'ANNOUNCEMENT')),
    CONSTRAINT ck_email_custom_templates_status CHECK (status IN ('ACTIVE', 'DELETED')),
    CONSTRAINT ck_email_custom_templates_deleted CHECK ((status = 'DELETED') = (deleted_at IS NOT NULL))
);

COMMENT ON TABLE notification.email_custom_templates IS
    'Admin-created email templates. Content is in email_content_variants under the same key; sent only by email_campaigns.';
COMMENT ON COLUMN notification.email_custom_templates.variables IS
    'JSON array of {name, label, type (TEXT|URL|DATE|NUMBER|MULTILINE), sample, required}.';

CREATE INDEX IF NOT EXISTS idx_email_custom_templates_status ON notification.email_custom_templates (status);

CREATE TABLE IF NOT EXISTS notification.email_campaigns (
    id uuid PRIMARY KEY,
    template_key varchar(60) NOT NULL,
    source varchar(20) NOT NULL DEFAULT 'MANUAL',
    announcement_id uuid,
    audience jsonb NOT NULL,
    values jsonb NOT NULL DEFAULT '{}'::jsonb,
    status varchar(20) NOT NULL DEFAULT 'QUEUED',
    scheduled_at timestamptz NOT NULL,
    started_at timestamptz,
    completed_at timestamptz,
    total_count integer NOT NULL DEFAULT 0,
    sent_count integer NOT NULL DEFAULT 0,
    failed_count integer NOT NULL DEFAULT 0,
    skipped_count integer NOT NULL DEFAULT 0,
    error varchar(500),
    created_by uuid NOT NULL,
    created_at timestamptz NOT NULL DEFAULT NOW(),
    cancelled_by uuid,
    cancelled_at timestamptz,
    CONSTRAINT ck_email_campaigns_source CHECK (source IN ('MANUAL', 'ANNOUNCEMENT')),
    CONSTRAINT ck_email_campaigns_status CHECK (status IN ('QUEUED', 'SENDING', 'COMPLETED', 'CANCELLED', 'FAILED')),
    CONSTRAINT ck_email_campaigns_counts CHECK (sent_count >= 0 AND failed_count >= 0 AND skipped_count >= 0 AND total_count >= 0)
);

CREATE INDEX IF NOT EXISTS idx_email_campaigns_template ON notification.email_campaigns (template_key, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_email_campaigns_due ON notification.email_campaigns (status, scheduled_at);
CREATE INDEX IF NOT EXISTS idx_email_campaigns_created_by ON notification.email_campaigns (created_by, created_at DESC);

CREATE TABLE IF NOT EXISTS notification.email_campaign_recipients (
    id uuid PRIMARY KEY,
    campaign_id uuid NOT NULL REFERENCES notification.email_campaigns (id) ON DELETE CASCADE,
    user_id uuid NOT NULL,
    email varchar(320) NOT NULL,
    full_name varchar(200),
    locale varchar(5) NOT NULL,
    status varchar(20) NOT NULL DEFAULT 'PENDING',
    error varchar(500),
    sent_at timestamptz,
    CONSTRAINT uq_email_campaign_recipients_user UNIQUE (campaign_id, user_id),
    CONSTRAINT ck_email_campaign_recipients_status CHECK (status IN ('PENDING', 'SENT', 'FAILED', 'SKIPPED'))
);

CREATE INDEX IF NOT EXISTS idx_email_campaign_recipients_pending
    ON notification.email_campaign_recipients (campaign_id, status);

ALTER TABLE notification.announcements
    ADD COLUMN IF NOT EXISTS email_template_key varchar(60),
    ADD COLUMN IF NOT EXISTS email_campaign_id uuid;

COMMENT ON COLUMN notification.announcements.email_template_key IS
    'Optional email channel: a custom template sent to the announcement''s audience when it goes live.';
