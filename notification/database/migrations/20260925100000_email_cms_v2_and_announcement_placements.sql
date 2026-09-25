-- Migration: 20260925100000_email_cms_v2_and_announcement_placements
-- Created At: 2026-09-25
-- Description:
--   Admin CMS v2: templates are separated from content, drafts from what is sent, and every
--   announcement placement the app renders becomes configurable.
--
--   EMAIL
--     email_blocks            Reusable design: LAYOUTs (the wrapper every email renders into,
--                             with {{content}}) and PARTIALs (blocks included with {{> key}}).
--                             Draft and published columns; senders read only published ones.
--     email_content_variants  One transactional email (an EmailTemplateCatalog key) in one
--                             locale (en / vi / ja): subject, preheader, heading, HTML, optional
--                             hand-written text, chosen layout — draft and published side.
--     email_cms_versions      Append-only snapshot of every publish (content or block), for the
--                             history, diff and restore.
--     email_sample_data_sets  Named sample values per email for the preview and test email.
--     email_delivery_stats    Sent / failed counters per email, locale and UTC day. There was no
--                             send log to count from; senders now report each send.
--
--     Backfill: every ACTIVE v1 row of notification_templates (channel EMAIL) becomes the English
--     variant with draft = published = the v1 content at the same version number, and its v1
--     history (notification_template_versions) becomes publish snapshots. The v1 tables are left
--     in place and are no longer read.
--
--   ANNOUNCEMENTS
--     announcements gains placement, variant, accent_color, icon, image_url, priority,
--     dismissible, frequency, target_roles, target_locales, new_users_within_days and a
--     secondary button. Defaults reproduce what a v1 announcement meant: a subtle top banner
--     shown until dismissed.
--     announcement_viewer_states  Per person: impressions, last session, dismissal, clicks —
--                                 what the show-frequency rules and the analytics read. Seeded
--                                 from announcement_dismissals (left in place, no longer read).
--     announcement_daily_stats    Impressions / dismissals / clicks per announcement and day.
--     announcement_assets         Uploaded announcement images (small, served by random id).
--
--   Additive and idempotent. No explicit transaction control: the migration runner owns the
--   transaction.

-- ── Email ───────────────────────────────────────────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS notification.email_blocks (
    id uuid PRIMARY KEY,
    kind varchar(20) NOT NULL,
    key varchar(60) NOT NULL,
    name varchar(120) NOT NULL,
    description varchar(500) NULL,
    status varchar(20) NOT NULL DEFAULT 'ACTIVE',
    is_default boolean NOT NULL DEFAULT false,
    draft_html text NOT NULL,
    draft_text text NULL,
    draft_dark_css text NULL,
    published_html text NULL,
    published_text text NULL,
    published_dark_css text NULL,
    published_version integer NOT NULL DEFAULT 0,
    published_at timestamptz NULL,
    published_by uuid NULL,
    draft_updated_at timestamptz NOT NULL DEFAULT now(),
    draft_updated_by uuid NOT NULL,
    archived_at timestamptz NULL,
    created_by uuid NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ck_email_blocks_kind CHECK (kind IN ('LAYOUT', 'PARTIAL')),
    CONSTRAINT ck_email_blocks_status CHECK (status IN ('ACTIVE', 'ARCHIVED')),
    CONSTRAINT uq_email_blocks_kind_key UNIQUE (kind, key)
);

-- At most one default layout.
CREATE UNIQUE INDEX IF NOT EXISTS uq_email_blocks_one_default_layout
    ON notification.email_blocks (kind) WHERE is_default AND kind = 'LAYOUT';

COMMENT ON TABLE notification.email_blocks IS 'Email layouts and reusable blocks (CMS v2). Senders read only the published_* columns.';
COMMENT ON COLUMN notification.email_blocks.created_by IS 'External AuthService user id. No physical FK.';

CREATE TABLE IF NOT EXISTS notification.email_content_variants (
    id uuid PRIMARY KEY,
    template_key varchar(60) NOT NULL,
    locale varchar(10) NOT NULL,
    status varchar(20) NOT NULL DEFAULT 'ACTIVE',
    draft_subject varchar(255) NOT NULL DEFAULT '',
    draft_preheader varchar(255) NOT NULL DEFAULT '',
    draft_heading varchar(255) NOT NULL DEFAULT '',
    draft_body_html text NOT NULL DEFAULT '',
    draft_text_body text NULL,
    draft_layout_id uuid NULL REFERENCES notification.email_blocks (id),
    published_subject varchar(255) NULL,
    published_preheader varchar(255) NULL,
    published_heading varchar(255) NULL,
    published_body_html text NULL,
    published_text_body text NULL,
    published_layout_id uuid NULL REFERENCES notification.email_blocks (id),
    published_version integer NOT NULL DEFAULT 0,
    published_at timestamptz NULL,
    published_by uuid NULL,
    draft_updated_at timestamptz NOT NULL DEFAULT now(),
    draft_updated_by uuid NOT NULL,
    archived_at timestamptz NULL,
    created_by uuid NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ck_email_content_variants_locale CHECK (locale IN ('en', 'vi', 'ja')),
    CONSTRAINT ck_email_content_variants_status CHECK (status IN ('ACTIVE', 'ARCHIVED')),
    CONSTRAINT uq_email_content_variants_key_locale UNIQUE (template_key, locale)
);

COMMENT ON TABLE notification.email_content_variants IS 'Transactional email content per catalog key and locale (CMS v2). Senders read only the published_* columns.';
COMMENT ON COLUMN notification.email_content_variants.template_key IS 'WarpTalk.Shared.Email.EmailTemplateCatalog key.';

CREATE TABLE IF NOT EXISTS notification.email_cms_versions (
    id uuid PRIMARY KEY,
    owner_type varchar(20) NOT NULL,
    owner_id uuid NOT NULL,
    version integer NOT NULL,
    action varchar(20) NOT NULL DEFAULT 'PUBLISHED',
    snapshot jsonb NOT NULL,
    note varchar(500) NULL,
    created_by uuid NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ck_email_cms_versions_owner_type CHECK (owner_type IN ('CONTENT', 'BLOCK')),
    CONSTRAINT uq_email_cms_versions_owner_version UNIQUE (owner_type, owner_id, version)
);

COMMENT ON TABLE notification.email_cms_versions IS 'Append-only snapshot of each publish of email content or a block.';

CREATE TABLE IF NOT EXISTS notification.email_sample_data_sets (
    id uuid PRIMARY KEY,
    template_key varchar(60) NOT NULL,
    name varchar(120) NOT NULL,
    values jsonb NOT NULL DEFAULT '{}'::jsonb,
    created_by uuid NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_by uuid NULL,
    updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_email_sample_data_sets_template ON notification.email_sample_data_sets (template_key);

CREATE TABLE IF NOT EXISTS notification.email_delivery_stats (
    template_key varchar(60) NOT NULL,
    locale varchar(10) NOT NULL,
    day date NOT NULL,
    sent_count integer NOT NULL DEFAULT 0,
    failed_count integer NOT NULL DEFAULT 0,
    PRIMARY KEY (template_key, locale, day)
);

COMMENT ON TABLE notification.email_delivery_stats IS 'Emails handed to a provider (sent) or refused by it (failed), per catalog key, locale and UTC day.';

-- Backfill the English variants from the v1 store. The v1 columns heading / version exist since
-- 20260924100000; created_by may be null on rows written by hand, so fall back to updated_by and
-- then the nil uuid.
INSERT INTO notification.email_content_variants (
    id, template_key, locale, status,
    draft_subject, draft_preheader, draft_heading, draft_body_html,
    published_subject, published_preheader, published_heading, published_body_html,
    published_version, published_at, published_by,
    draft_updated_at, draft_updated_by, created_by, created_at)
SELECT gen_random_uuid(), t.type, 'en', 'ACTIVE',
       COALESCE(t.subject, ''), '', COALESCE(t.heading, ''), t.body_template,
       COALESCE(t.subject, ''), '', COALESCE(t.heading, ''), t.body_template,
       GREATEST(t.version, 1), t.updated_at, t.updated_by,
       t.updated_at,
       COALESCE(t.updated_by, t.created_by, '00000000-0000-0000-0000-000000000000'::uuid),
       COALESCE(t.created_by, t.updated_by, '00000000-0000-0000-0000-000000000000'::uuid),
       t.created_at
FROM notification.notification_templates t
WHERE t.channel = 'EMAIL' AND t.is_active
ON CONFLICT (template_key, locale) DO NOTHING;

INSERT INTO notification.email_cms_versions (id, owner_type, owner_id, version, action, snapshot, note, created_by, created_at)
SELECT gen_random_uuid(), 'CONTENT', v.id, h.version, 'PUBLISHED',
       jsonb_build_object(
           'subject', h.subject,
           'preheader', '',
           'heading', h.heading,
           'bodyHtml', h.body_template,
           'textBody', NULL,
           'layoutId', NULL),
       COALESCE(h.note, 'Migrated from the v1 email template history (' || lower(h.action) || ').'),
       h.created_by, h.created_at
FROM notification.notification_template_versions h
JOIN notification.email_content_variants v ON v.template_key = h.template_type AND v.locale = 'en'
WHERE h.channel = 'EMAIL'
ON CONFLICT (owner_type, owner_id, version) DO NOTHING;

-- ── Announcements ───────────────────────────────────────────────────────────────────────────────

ALTER TABLE notification.announcements
    ADD COLUMN IF NOT EXISTS placement varchar(30) NOT NULL DEFAULT 'TOP_BANNER'
        CONSTRAINT ck_announcements_placement CHECK (placement IN ('TOP_BANNER', 'MODAL', 'TOAST', 'NOTIFICATION_CENTER', 'DASHBOARD_CARD')),
    ADD COLUMN IF NOT EXISTS variant varchar(20) NOT NULL DEFAULT 'SUBTLE'
        CONSTRAINT ck_announcements_variant CHECK (variant IN ('SUBTLE', 'SOLID', 'OUTLINE')),
    ADD COLUMN IF NOT EXISTS accent_color varchar(20) NOT NULL DEFAULT 'BRAND'
        CONSTRAINT ck_announcements_accent_color CHECK (accent_color IN ('BRAND', 'BLUE', 'GREEN', 'AMBER', 'RED', 'VIOLET', 'NEUTRAL')),
    ADD COLUMN IF NOT EXISTS icon varchar(40) NULL,
    ADD COLUMN IF NOT EXISTS image_url varchar(2048) NULL,
    ADD COLUMN IF NOT EXISTS priority integer NOT NULL DEFAULT 0
        CONSTRAINT ck_announcements_priority CHECK (priority BETWEEN 0 AND 100),
    ADD COLUMN IF NOT EXISTS dismissible boolean NOT NULL DEFAULT true,
    ADD COLUMN IF NOT EXISTS frequency varchar(20) NOT NULL DEFAULT 'UNTIL_DISMISSED'
        CONSTRAINT ck_announcements_frequency CHECK (frequency IN ('UNTIL_DISMISSED', 'ONCE', 'EVERY_SESSION', 'DAILY')),
    ADD COLUMN IF NOT EXISTS target_roles text[] NOT NULL DEFAULT '{}',
    ADD COLUMN IF NOT EXISTS target_locales text[] NOT NULL DEFAULT '{}',
    ADD COLUMN IF NOT EXISTS new_users_within_days integer NULL
        CONSTRAINT ck_announcements_new_users CHECK (new_users_within_days IS NULL OR new_users_within_days BETWEEN 1 AND 365),
    ADD COLUMN IF NOT EXISTS secondary_cta_label varchar(60) NULL,
    ADD COLUMN IF NOT EXISTS secondary_cta_url varchar(2048) NULL;

CREATE INDEX IF NOT EXISTS idx_announcements_placement ON notification.announcements (placement);

CREATE TABLE IF NOT EXISTS notification.announcement_viewer_states (
    announcement_id uuid NOT NULL REFERENCES notification.announcements (id) ON DELETE CASCADE,
    user_id uuid NOT NULL,
    impression_count integer NOT NULL DEFAULT 0,
    first_seen_at timestamptz NULL,
    last_seen_at timestamptz NULL,
    last_session_id varchar(64) NULL,
    dismissed_at timestamptz NULL,
    dismissed_session_id varchar(64) NULL,
    cta_click_count integer NOT NULL DEFAULT 0,
    secondary_click_count integer NOT NULL DEFAULT 0,
    last_clicked_at timestamptz NULL,
    PRIMARY KEY (announcement_id, user_id)
);

CREATE INDEX IF NOT EXISTS idx_announcement_viewer_states_user ON notification.announcement_viewer_states (user_id);

COMMENT ON COLUMN notification.announcement_viewer_states.user_id IS 'External AuthService user id. No physical FK.';

INSERT INTO notification.announcement_viewer_states (announcement_id, user_id, dismissed_at)
SELECT d.announcement_id, d.user_id, d.dismissed_at
FROM notification.announcement_dismissals d
ON CONFLICT (announcement_id, user_id) DO NOTHING;

CREATE TABLE IF NOT EXISTS notification.announcement_daily_stats (
    announcement_id uuid NOT NULL REFERENCES notification.announcements (id) ON DELETE CASCADE,
    day date NOT NULL,
    impressions integer NOT NULL DEFAULT 0,
    dismissals integer NOT NULL DEFAULT 0,
    cta_clicks integer NOT NULL DEFAULT 0,
    secondary_clicks integer NOT NULL DEFAULT 0,
    PRIMARY KEY (announcement_id, day)
);

CREATE TABLE IF NOT EXISTS notification.announcement_assets (
    id uuid PRIMARY KEY,
    file_name varchar(255) NOT NULL,
    content_type varchar(50) NOT NULL,
    size_bytes integer NOT NULL,
    content bytea NOT NULL,
    created_by uuid NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ck_announcement_assets_type CHECK (content_type IN ('image/png', 'image/jpeg', 'image/webp', 'image/gif')),
    CONSTRAINT ck_announcement_assets_size CHECK (size_bytes BETWEEN 1 AND 2097152)
);

COMMENT ON TABLE notification.announcement_assets IS 'Images uploaded for announcements. Served anonymously by random id.';
