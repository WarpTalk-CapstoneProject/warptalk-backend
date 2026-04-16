-- ==============================================================================
-- WarpTalk Microservices - PostgreSQL Initialization Script
-- ==============================================================================
-- This script creates the isolated schemas and users for each microservice.
-- It also sets up standard tables, indexes, and partitioning patterns.

-- Enable crypto extension for random bytes (used in UUID v7)
CREATE EXTENSION IF NOT EXISTS "pgcrypto";

-- ============================================
-- 0. UUID V7 GENERATOR (Polyfill for < PG 17)
-- ============================================
CREATE OR REPLACE FUNCTION uuid_generate_v7()
RETURNS uuid
AS $$
DECLARE
    unix_ts_ms bytea;
    uuid_bytes bytea;
BEGIN
    unix_ts_ms = substring(int8send(floor(extract(epoch from clock_timestamp()) * 1000)::bigint) from 3);
    uuid_bytes = unix_ts_ms || gen_random_bytes(10);
    -- Set version 7: clear top 4 bits of byte 6, set to 0111 (0x70)
    uuid_bytes = set_byte(uuid_bytes, 6, (get_byte(uuid_bytes, 6) & 15) | 112);
    -- Set variant 2: clear top 2 bits of byte 8, set to 10 (0x80)
    uuid_bytes = set_byte(uuid_bytes, 8, (get_byte(uuid_bytes, 8) & 63) | 128);
    RETURN encode(uuid_bytes, 'hex')::uuid;
END
$$ LANGUAGE plpgsql VOLATILE;

-- ============================================
-- 1. SERVICE_ACCOUNTS & ISOLATION
-- ============================================
CREATE USER auth_svc WITH ENCRYPTED PASSWORD 'warptalk_auth_dev';
CREATE USER translation_room_svc WITH ENCRYPTED PASSWORD 'warptalk_translation_room_dev';
CREATE USER transcript_svc WITH ENCRYPTED PASSWORD 'warptalk_transcript_dev';
CREATE USER sub_svc WITH ENCRYPTED PASSWORD 'warptalk_sub_dev';
CREATE USER notif_svc WITH ENCRYPTED PASSWORD 'warptalk_notif_dev';

-- ============================================
-- 2. AUTH SCHEMA
-- ============================================
CREATE SCHEMA IF NOT EXISTS auth;

CREATE TABLE auth.users (
    id              UUID PRIMARY KEY DEFAULT uuid_generate_v7(),
    email           VARCHAR(320) UNIQUE NOT NULL,
    password_hash   VARCHAR(255) NOT NULL,
    full_name       VARCHAR(150) NOT NULL,
    avatar_url      VARCHAR(500),
    phone           VARCHAR(20),
    preferred_language CHAR(5) DEFAULT 'vi-VN',
    timezone        VARCHAR(50) DEFAULT 'Asia/Ho_Chi_Minh',
    is_active       BOOLEAN NOT NULL DEFAULT true,
    is_locked       BOOLEAN NOT NULL DEFAULT false,
    failed_login_attempts INT NOT NULL DEFAULT 0 CHECK (failed_login_attempts >= 0),
    locked_until    TIMESTAMPTZ,
    email_verified  BOOLEAN NOT NULL DEFAULT false,
    email_verified_at TIMESTAMPTZ,
    google_id       VARCHAR(255) UNIQUE,
    last_login_at   TIMESTAMPTZ,
    last_login_ip   VARCHAR(45),
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    deleted_at      TIMESTAMPTZ
);

CREATE TABLE auth.roles (
    id          UUID PRIMARY KEY DEFAULT uuid_generate_v7(),
    name        VARCHAR(50) UNIQUE NOT NULL,
    description VARCHAR(255),
    is_system   BOOLEAN NOT NULL DEFAULT false,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE auth.permissions (
    id          UUID PRIMARY KEY DEFAULT uuid_generate_v7(),
    code        VARCHAR(100) UNIQUE NOT NULL,
    description VARCHAR(255),
    group_name  VARCHAR(50) NOT NULL
);

CREATE TABLE auth.role_permissions (
    role_id       UUID NOT NULL REFERENCES auth.roles(id) ON DELETE CASCADE,
    permission_id UUID NOT NULL REFERENCES auth.permissions(id) ON DELETE CASCADE,
    PRIMARY KEY (role_id, permission_id)
);

CREATE TABLE auth.user_roles (
    id           UUID PRIMARY KEY DEFAULT uuid_generate_v7(),
    user_id      UUID NOT NULL REFERENCES auth.users(id) ON DELETE CASCADE,
    role_id      UUID NOT NULL REFERENCES auth.roles(id) ON DELETE RESTRICT,
    workspace_id UUID,
    assigned_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    assigned_by  UUID REFERENCES auth.users(id) ON DELETE SET NULL,
    UNIQUE (user_id, role_id, workspace_id)
);

CREATE TABLE auth.workspaces (
    id         UUID PRIMARY KEY DEFAULT uuid_generate_v7(),
    name       VARCHAR(150) NOT NULL,
    slug       VARCHAR(100) UNIQUE NOT NULL,
    owner_id   UUID NOT NULL REFERENCES auth.users(id) ON DELETE RESTRICT,
    logo_url   VARCHAR(500),
    plan_tier  VARCHAR(30) NOT NULL DEFAULT 'free',
    settings   JSONB NOT NULL DEFAULT '{}',
    is_active  BOOLEAN NOT NULL DEFAULT true,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    deleted_at TIMESTAMPTZ
);

CREATE TABLE auth.workspace_invitations (
    id           UUID PRIMARY KEY DEFAULT uuid_generate_v7(),
    workspace_id UUID NOT NULL REFERENCES auth.workspaces(id) ON DELETE CASCADE,
    email        VARCHAR(320) NOT NULL,
    role_id      UUID NOT NULL REFERENCES auth.roles(id) ON DELETE RESTRICT,
    invited_by   UUID NOT NULL REFERENCES auth.users(id) ON DELETE CASCADE,
    token        VARCHAR(128) UNIQUE NOT NULL,
    status       VARCHAR(20) NOT NULL DEFAULT 'pending'
                 CHECK (status IN ('pending','accepted','expired','revoked')),
    expires_at   TIMESTAMPTZ NOT NULL,
    accepted_at  TIMESTAMPTZ,
    created_at   TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE auth.refresh_tokens (
    id          UUID PRIMARY KEY DEFAULT uuid_generate_v7(),
    user_id     UUID NOT NULL REFERENCES auth.users(id) ON DELETE CASCADE,
    token_hash  VARCHAR(255) UNIQUE NOT NULL,
    device_info VARCHAR(255),
    ip_address  VARCHAR(45),
    expires_at  TIMESTAMPTZ NOT NULL,
    revoked_at  TIMESTAMPTZ,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE auth.user_settings (
    id                        UUID PRIMARY KEY DEFAULT uuid_generate_v7(),
    user_id                   UUID UNIQUE NOT NULL REFERENCES auth.users(id) ON DELETE CASCADE,
    default_speak_language    CHAR(5) NOT NULL DEFAULT 'vi-VN',
    default_listen_language   CHAR(5) NOT NULL DEFAULT 'en-US',
    voice_clone_enabled       BOOLEAN NOT NULL DEFAULT false,
    mic_noise_suppression     BOOLEAN NOT NULL DEFAULT true,
    default_translation_room_type      VARCHAR(20) NOT NULL DEFAULT 'group'
                              CHECK (default_translation_room_type IN ('one_to_one','group','webinar','b2b_virtual_mic')),
    auto_record_translation_rooms      BOOLEAN NOT NULL DEFAULT false,
    auto_generate_summary     BOOLEAN NOT NULL DEFAULT true,
    default_max_participants  INT NOT NULL DEFAULT 10 CHECK (default_max_participants BETWEEN 1 AND 500),
    theme                     VARCHAR(10) NOT NULL DEFAULT 'system'
                              CHECK (theme IN ('light','dark','system')),
    transcript_font_size      INT NOT NULL DEFAULT 14 CHECK (transcript_font_size BETWEEN 10 AND 32),
    show_original_transcript  BOOLEAN NOT NULL DEFAULT true,
    show_translated_transcript BOOLEAN NOT NULL DEFAULT true,
    compact_participant_list  BOOLEAN NOT NULL DEFAULT false,
    high_contrast             BOOLEAN NOT NULL DEFAULT false,
    screen_reader_mode        BOOLEAN NOT NULL DEFAULT false,
    updated_at                TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE auth.audit_logs (
    id          UUID NOT NULL DEFAULT uuid_generate_v7(),
    user_id     UUID,
    action      VARCHAR(100) NOT NULL,
    entity_type VARCHAR(50) NOT NULL,
    entity_id   UUID,
    old_values  JSONB,
    new_values  JSONB,
    ip_address  VARCHAR(45),
    user_agent  VARCHAR(500),
    created_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (id, created_at)
) PARTITION BY RANGE (created_at);

CREATE TABLE auth.audit_logs_y2025 PARTITION OF auth.audit_logs
    FOR VALUES FROM ('2025-01-01') TO ('2026-01-01');
CREATE TABLE auth.audit_logs_y2026 PARTITION OF auth.audit_logs
    FOR VALUES FROM ('2026-01-01') TO ('2027-01-01');
CREATE TABLE auth.audit_logs_y2027 PARTITION OF auth.audit_logs
    FOR VALUES FROM ('2027-01-01') TO ('2028-01-01');
CREATE TABLE auth.audit_logs_default PARTITION OF auth.audit_logs DEFAULT;

-- Indexes for Auth
CREATE INDEX idx_users_email ON auth.users(email) WHERE deleted_at IS NULL;
CREATE INDEX idx_users_deleted ON auth.users(deleted_at) WHERE deleted_at IS NOT NULL;
CREATE INDEX idx_user_roles_user ON auth.user_roles(user_id);
CREATE INDEX idx_user_roles_workspace ON auth.user_roles(workspace_id);
CREATE INDEX idx_workspaces_owner ON auth.workspaces(owner_id);
CREATE INDEX idx_workspaces_slug ON auth.workspaces(slug) WHERE deleted_at IS NULL;
CREATE INDEX idx_invitations_workspace ON auth.workspace_invitations(workspace_id);
CREATE INDEX idx_invitations_email ON auth.workspace_invitations(email, status);
CREATE INDEX idx_refresh_tokens_user ON auth.refresh_tokens(user_id);
CREATE INDEX idx_user_settings_user ON auth.user_settings(user_id);
CREATE INDEX idx_audit_logs_user ON auth.audit_logs(user_id);
CREATE INDEX idx_audit_logs_entity ON auth.audit_logs(entity_type, entity_id);
CREATE INDEX idx_audit_logs_created ON auth.audit_logs(created_at DESC);

-- Grants
GRANT USAGE ON SCHEMA auth TO auth_svc;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA auth TO auth_svc;
ALTER DEFAULT PRIVILEGES IN SCHEMA auth GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO auth_svc;


-- ============================================
-- 3. TRANSLATION ROOM SCHEMA
-- ============================================
CREATE SCHEMA IF NOT EXISTS translation_room;

CREATE TABLE translation_room.translation_rooms (
    id               UUID PRIMARY KEY DEFAULT uuid_generate_v7(),
    workspace_id     UUID NOT NULL,
    host_id          UUID NOT NULL,
    title            VARCHAR(255) NOT NULL,
    description      TEXT,
    translation_room_code     VARCHAR(12) UNIQUE NOT NULL,
    status           VARCHAR(20) NOT NULL DEFAULT 'scheduled'
                     CHECK (status IN ('scheduled','waiting','in_progress','ended','archived','cancelled')),
    translation_room_type     VARCHAR(20) NOT NULL DEFAULT 'group'
                     CHECK (translation_room_type IN ('one_to_one','group','webinar','b2b_virtual_mic')),
    max_participants INT NOT NULL DEFAULT 10 CHECK (max_participants >= 1 AND max_participants <= 500),
    source_language  CHAR(5) NOT NULL,
    target_languages JSONB NOT NULL DEFAULT '[]',
    settings         JSONB NOT NULL DEFAULT '{}',
    scheduled_at     TIMESTAMPTZ,
    started_at       TIMESTAMPTZ,
    ended_at         TIMESTAMPTZ,
    duration_seconds INT CHECK (duration_seconds >= 0),
    created_at       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    deleted_at       TIMESTAMPTZ
);

CREATE TABLE translation_room.translation_room_participants (
    id                   UUID PRIMARY KEY DEFAULT uuid_generate_v7(),
    translation_room_id           UUID NOT NULL REFERENCES translation_room.translation_rooms(id) ON DELETE CASCADE,
    user_id              UUID NOT NULL,
    display_name         VARCHAR(100) NOT NULL,
    role                 VARCHAR(20) NOT NULL DEFAULT 'participant'
                         CHECK (role IN ('host','co_host','participant','observer')),
    listen_language      CHAR(5) NOT NULL,
    speak_language       CHAR(5) NOT NULL,
    status               VARCHAR(20) NOT NULL DEFAULT 'invited'
                         CHECK (status IN ('invited','waiting','connected','disconnected','kicked')),
    connection_type      VARCHAR(20) DEFAULT 'webrtc',
    is_muted             BOOLEAN NOT NULL DEFAULT false,
    is_using_voice_clone BOOLEAN NOT NULL DEFAULT false,
    joined_at            TIMESTAMPTZ,
    left_at              TIMESTAMPTZ,
    updated_at           TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE translation_room.translation_room_audio_routes (
    id                     UUID PRIMARY KEY DEFAULT uuid_generate_v7(),
    translation_room_id             UUID NOT NULL REFERENCES translation_room.translation_rooms(id) ON DELETE CASCADE,
    source_participant_id  UUID NOT NULL REFERENCES translation_room.translation_room_participants(id) ON DELETE CASCADE,
    target_participant_id  UUID NOT NULL REFERENCES translation_room.translation_room_participants(id) ON DELETE CASCADE,
    source_language        CHAR(5) NOT NULL,
    target_language        CHAR(5) NOT NULL,
    voice_clone_enabled    BOOLEAN NOT NULL DEFAULT false,
    stream_id              VARCHAR(100),
    status                 VARCHAR(20) NOT NULL DEFAULT 'active'
                           CHECK (status IN ('active','paused','ended')),
    created_at             TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CHECK (source_participant_id != target_participant_id)
);

CREATE TABLE translation_room.translation_room_recordings (
    id              UUID PRIMARY KEY DEFAULT uuid_generate_v7(),
    translation_room_id      UUID NOT NULL REFERENCES translation_room.translation_rooms(id) ON DELETE CASCADE,
    recording_type  VARCHAR(20) NOT NULL CHECK (recording_type IN ('audio','video','transcript')),
    file_url        VARCHAR(500) NOT NULL,
    file_format     VARCHAR(10) NOT NULL,
    file_size_bytes BIGINT NOT NULL CHECK (file_size_bytes > 0),
    duration_seconds INT NOT NULL CHECK (duration_seconds >= 0),
    language        CHAR(5),
    status          VARCHAR(20) NOT NULL DEFAULT 'processing'
                    CHECK (status IN ('processing','ready','failed','deleted')),
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE translation_room.translation_room_summaries (
    id                UUID PRIMARY KEY DEFAULT uuid_generate_v7(),
    translation_room_id        UUID NOT NULL REFERENCES translation_room.translation_rooms(id) ON DELETE CASCADE,
    summary           TEXT NOT NULL,
    key_points        JSONB NOT NULL DEFAULT '[]',
    decisions         JSONB NOT NULL DEFAULT '[]',
    action_items      JSONB NOT NULL DEFAULT '[]',
    model_used        VARCHAR(50) NOT NULL,
    processing_time_ms INT NOT NULL CHECK (processing_time_ms >= 0),
    generated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE (translation_room_id)
);

CREATE TABLE translation_room.translation_room_feedback (
    id                    UUID PRIMARY KEY DEFAULT uuid_generate_v7(),
    translation_room_id            UUID NOT NULL REFERENCES translation_room.translation_rooms(id) ON DELETE CASCADE,
    user_id               UUID NOT NULL,
    overall_rating        INT NOT NULL CHECK (overall_rating BETWEEN 1 AND 5),
    translation_quality   INT CHECK (translation_quality BETWEEN 1 AND 5),
    audio_quality         INT CHECK (audio_quality BETWEEN 1 AND 5),
    voice_clone_quality   INT CHECK (voice_clone_quality BETWEEN 1 AND 5),
    comments              TEXT,
    communication_insights JSONB,
    created_at            TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE (translation_room_id, user_id)
);

-- Indexes for Meeting
CREATE INDEX idx_translation_rooms_workspace ON translation_room.translation_rooms(workspace_id);
CREATE INDEX idx_translation_rooms_host ON translation_room.translation_rooms(host_id);
CREATE INDEX idx_translation_rooms_status ON translation_room.translation_rooms(status) WHERE deleted_at IS NULL;
CREATE INDEX idx_translation_rooms_scheduled ON translation_room.translation_rooms(scheduled_at DESC) WHERE deleted_at IS NULL;
CREATE INDEX idx_participants_translation_room ON translation_room.translation_room_participants(translation_room_id);
CREATE INDEX idx_participants_user ON translation_room.translation_room_participants(user_id);
CREATE INDEX idx_audio_routes_translation_room ON translation_room.translation_room_audio_routes(translation_room_id);
CREATE INDEX idx_recordings_translation_room ON translation_room.translation_room_recordings(translation_room_id);
CREATE INDEX idx_feedback_translation_room ON translation_room.translation_room_feedback(translation_room_id);

-- Grants
GRANT USAGE ON SCHEMA translation_room TO translation_room_svc;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA translation_room TO translation_room_svc;
ALTER DEFAULT PRIVILEGES IN SCHEMA translation_room GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO translation_room_svc;


-- ============================================
-- 4. TRANSCRIPT SCHEMA
-- ============================================
CREATE SCHEMA IF NOT EXISTS transcript;

CREATE TABLE transcript.transcripts (
    id              UUID PRIMARY KEY DEFAULT uuid_generate_v7(),
    translation_room_id      UUID NOT NULL,
    version         INT NOT NULL DEFAULT 1 CHECK (version >= 1),
    status          VARCHAR(20) NOT NULL DEFAULT 'recording'
                    CHECK (status IN ('recording','processing','finalized','archived')),
    source_language CHAR(5) NOT NULL,
    total_segments  INT NOT NULL DEFAULT 0 CHECK (total_segments >= 0),
    total_duration_ms INT NOT NULL DEFAULT 0 CHECK (total_duration_ms >= 0),
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    finalized_at    TIMESTAMPTZ,
    deleted_at      TIMESTAMPTZ,
    UNIQUE (translation_room_id, version)
);

CREATE TABLE transcript.transcript_segments (
    id                UUID NOT NULL DEFAULT uuid_generate_v7(),
    transcript_id     UUID NOT NULL,
    speaker_id        UUID NOT NULL,
    speaker_name      VARCHAR(100) NOT NULL,
    original_text     TEXT NOT NULL,
    original_language CHAR(5) NOT NULL,
    start_time_ms     INT NOT NULL CHECK (start_time_ms >= 0),
    end_time_ms       INT NOT NULL CHECK (end_time_ms >= 0),
    confidence        FLOAT NOT NULL CHECK (confidence BETWEEN 0.0 AND 1.0),
    sequence_order    INT NOT NULL CHECK (sequence_order >= 0),
    is_corrected      BOOLEAN NOT NULL DEFAULT false,
    created_at        TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at        TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (id, created_at),
    CHECK (end_time_ms > start_time_ms)
) PARTITION BY RANGE (created_at);

CREATE TABLE transcript.transcript_segments_y2025 PARTITION OF transcript.transcript_segments
    FOR VALUES FROM ('2025-01-01') TO ('2026-01-01');
CREATE TABLE transcript.transcript_segments_y2026 PARTITION OF transcript.transcript_segments
    FOR VALUES FROM ('2026-01-01') TO ('2027-01-01');
CREATE TABLE transcript.transcript_segments_y2027 PARTITION OF transcript.transcript_segments
    FOR VALUES FROM ('2027-01-01') TO ('2028-01-01');
CREATE TABLE transcript.transcript_segments_default PARTITION OF transcript.transcript_segments DEFAULT;

CREATE TABLE transcript.transcript_translations (
    id               UUID PRIMARY KEY DEFAULT uuid_generate_v7(),
    segment_id       UUID NOT NULL,
    target_language  CHAR(5) NOT NULL,
    translated_text  TEXT NOT NULL,
    translator_model VARCHAR(50) NOT NULL,
    confidence       FLOAT NOT NULL CHECK (confidence BETWEEN 0.0 AND 1.0),
    is_retranslated  BOOLEAN NOT NULL DEFAULT false,
    latency_ms       INT CHECK (latency_ms >= 0),
    created_at       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE (segment_id, target_language)
);

CREATE TABLE transcript.transcript_corrections (
    id                       UUID PRIMARY KEY DEFAULT uuid_generate_v7(),
    segment_id               UUID NOT NULL,
    user_id                  UUID NOT NULL,
    original_text            TEXT NOT NULL,
    corrected_text           TEXT NOT NULL,
    correction_type          VARCHAR(20) NOT NULL
                             CHECK (correction_type IN ('spelling','grammar','context','terminology')),
    triggered_retranslation  BOOLEAN NOT NULL DEFAULT false,
    created_at               TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE transcript.transcript_exports (
    id                  UUID PRIMARY KEY DEFAULT uuid_generate_v7(),
    transcript_id       UUID NOT NULL REFERENCES transcript.transcripts(id) ON DELETE CASCADE,
    user_id             UUID NOT NULL,
    format              VARCHAR(10) NOT NULL CHECK (format IN ('txt','srt','vtt','pdf','docx')),
    file_url            VARCHAR(500) NOT NULL,
    included_languages  JSONB NOT NULL DEFAULT '[]',
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE transcript.glossaries (
    id              UUID PRIMARY KEY DEFAULT uuid_generate_v7(),
    workspace_id    UUID NOT NULL,
    name            VARCHAR(150) NOT NULL,
    description     TEXT,
    source_language CHAR(5) NOT NULL,
    target_language CHAR(5) NOT NULL,
    is_active       BOOLEAN NOT NULL DEFAULT true,
    term_count      INT NOT NULL DEFAULT 0 CHECK (term_count >= 0),
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    deleted_at      TIMESTAMPTZ,
    UNIQUE (workspace_id, name)
);

CREATE TABLE transcript.glossary_terms (
    id          UUID PRIMARY KEY DEFAULT uuid_generate_v7(),
    glossary_id UUID NOT NULL REFERENCES transcript.glossaries(id) ON DELETE CASCADE,
    source_term VARCHAR(255) NOT NULL,
    target_term VARCHAR(255) NOT NULL,
    context     TEXT,
    domain      VARCHAR(50),
    priority    INT NOT NULL DEFAULT 5 CHECK (priority BETWEEN 1 AND 10),
    created_by  UUID NOT NULL,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE (glossary_id, source_term)
);

-- Indexes for Transcript
CREATE INDEX idx_transcripts_translation_room ON transcript.transcripts(translation_room_id);
CREATE INDEX idx_segments_transcript ON transcript.transcript_segments(transcript_id);
CREATE INDEX idx_segments_transcript_order ON transcript.transcript_segments(transcript_id, sequence_order);
CREATE INDEX idx_segments_speaker ON transcript.transcript_segments(speaker_id);
CREATE INDEX idx_translations_segment ON transcript.transcript_translations(segment_id);
CREATE INDEX idx_corrections_segment ON transcript.transcript_corrections(segment_id);
CREATE INDEX idx_exports_transcript ON transcript.transcript_exports(transcript_id);
CREATE INDEX idx_glossaries_workspace ON transcript.glossaries(workspace_id) WHERE deleted_at IS NULL;
CREATE INDEX idx_glossary_terms_glossary ON transcript.glossary_terms(glossary_id);
CREATE INDEX idx_glossary_terms_source ON transcript.glossary_terms(glossary_id, source_term);

-- Full-text search
CREATE INDEX idx_segments_fts ON transcript.transcript_segments USING GIN(to_tsvector('simple', original_text));
CREATE INDEX idx_glossary_fts ON transcript.glossary_terms USING GIN(to_tsvector('simple', source_term || ' ' || target_term));

-- Grants
GRANT USAGE ON SCHEMA transcript TO transcript_svc;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA transcript TO transcript_svc;
ALTER DEFAULT PRIVILEGES IN SCHEMA transcript GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO transcript_svc;


-- ============================================
-- 5. SUBSCRIPTION SCHEMA
-- ============================================
CREATE SCHEMA IF NOT EXISTS subscription;

CREATE TABLE subscription.plans (
    id                   UUID PRIMARY KEY DEFAULT uuid_generate_v7(),
    name                 VARCHAR(100) NOT NULL,
    slug                 VARCHAR(50) UNIQUE NOT NULL,
    tier                 VARCHAR(20) NOT NULL
                         CHECK (tier IN ('free','pro','premium','enterprise')),
    price                DECIMAL(12, 2) NOT NULL CHECK (price >= 0),
    currency             CHAR(3) NOT NULL DEFAULT 'VND',
    billing_cycle        VARCHAR(20) NOT NULL DEFAULT 'monthly'
                         CHECK (billing_cycle IN ('monthly','quarterly','yearly','one_time')),
    credits_per_cycle    INT NOT NULL CHECK (credits_per_cycle >= 0),
    max_participants     INT NOT NULL DEFAULT 2 CHECK (max_participants >= 1),
    max_languages        INT NOT NULL DEFAULT 2 CHECK (max_languages >= 1),
    voice_clone_enabled  BOOLEAN NOT NULL DEFAULT false,
    ai_assistant_enabled BOOLEAN NOT NULL DEFAULT false,
    glossary_enabled     BOOLEAN NOT NULL DEFAULT false,
    dedicated_gpu        BOOLEAN NOT NULL DEFAULT false,
    features             JSONB NOT NULL DEFAULT '{}',
    is_active            BOOLEAN NOT NULL DEFAULT true,
    sort_order           INT NOT NULL DEFAULT 0,
    created_at           TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at           TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE subscription.subscriptions (
    id                    UUID PRIMARY KEY DEFAULT uuid_generate_v7(),
    user_id               UUID NOT NULL,
    workspace_id          UUID,
    plan_id               UUID NOT NULL REFERENCES subscription.plans(id) ON DELETE RESTRICT,
    status                VARCHAR(20) NOT NULL DEFAULT 'active'
                          CHECK (status IN ('active','past_due','cancelled','expired','trialing','suspended')),
    credits_remaining     INT NOT NULL DEFAULT 0 CHECK (credits_remaining >= 0),
    credits_used_this_cycle INT NOT NULL DEFAULT 0 CHECK (credits_used_this_cycle >= 0),
    current_period_start  TIMESTAMPTZ NOT NULL,
    current_period_end    TIMESTAMPTZ NOT NULL,
    auto_renew            BOOLEAN NOT NULL DEFAULT true,
    cancellation_reason   TEXT,
    cancelled_at          TIMESTAMPTZ,
    trial_ends_at         TIMESTAMPTZ,
    created_at            TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at            TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    deleted_at            TIMESTAMPTZ,
    CHECK (current_period_end > current_period_start)
);

CREATE TABLE subscription.credit_transactions (
    id              UUID NOT NULL DEFAULT uuid_generate_v7(),
    subscription_id UUID NOT NULL REFERENCES subscription.subscriptions(id) ON DELETE RESTRICT,
    user_id         UUID NOT NULL,
    amount          INT NOT NULL,
    type            VARCHAR(20) NOT NULL
                    CHECK (type IN ('allocation','consumption','bonus','refund','adjustment','expiry')),
    description     VARCHAR(255),
    reference_id    UUID,
    reference_type  VARCHAR(30),
    balance_after   INT NOT NULL CHECK (balance_after >= 0),
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (id, created_at)
) PARTITION BY RANGE (created_at);

CREATE TABLE subscription.credit_transactions_y2025 PARTITION OF subscription.credit_transactions
    FOR VALUES FROM ('2025-01-01') TO ('2026-01-01');
CREATE TABLE subscription.credit_transactions_y2026 PARTITION OF subscription.credit_transactions
    FOR VALUES FROM ('2026-01-01') TO ('2027-01-01');
CREATE TABLE subscription.credit_transactions_y2027 PARTITION OF subscription.credit_transactions
    FOR VALUES FROM ('2027-01-01') TO ('2028-01-01');
CREATE TABLE subscription.credit_transactions_default PARTITION OF subscription.credit_transactions DEFAULT;

CREATE TABLE subscription.payments (
    id                      UUID PRIMARY KEY DEFAULT uuid_generate_v7(),
    subscription_id         UUID NOT NULL REFERENCES subscription.subscriptions(id) ON DELETE RESTRICT,
    user_id                 UUID NOT NULL,
    amount                  DECIMAL(12, 2) NOT NULL CHECK (amount >= 0),
    tax_amount              DECIMAL(12, 2) NOT NULL DEFAULT 0 CHECK (tax_amount >= 0),
    total_amount            DECIMAL(12, 2) NOT NULL CHECK (total_amount >= 0),
    currency                CHAR(3) NOT NULL DEFAULT 'VND',
    payment_method          VARCHAR(30) NOT NULL,
    provider                VARCHAR(30) NOT NULL DEFAULT 'payos',
    provider_transaction_id VARCHAR(255) UNIQUE,
    provider_order_id       VARCHAR(255),
    status                  VARCHAR(20) NOT NULL DEFAULT 'pending'
                            CHECK (status IN ('pending','processing','completed','failed','refunded','partially_refunded')),
    failure_reason          VARCHAR(500),
    provider_metadata       JSONB,
    paid_at                 TIMESTAMPTZ,
    refunded_at             TIMESTAMPTZ,
    created_at              TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at              TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE subscription.invoices (
    id              UUID PRIMARY KEY DEFAULT uuid_generate_v7(),
    payment_id      UUID NOT NULL REFERENCES subscription.payments(id) ON DELETE RESTRICT,
    user_id         UUID NOT NULL,
    invoice_number  VARCHAR(30) UNIQUE NOT NULL,
    subtotal        DECIMAL(12, 2) NOT NULL CHECK (subtotal >= 0),
    tax             DECIMAL(12, 2) NOT NULL DEFAULT 0 CHECK (tax >= 0),
    total           DECIMAL(12, 2) NOT NULL CHECK (total >= 0),
    currency        CHAR(3) NOT NULL DEFAULT 'VND',
    status          VARCHAR(20) NOT NULL DEFAULT 'issued'
                    CHECK (status IN ('draft','issued','paid','void')),
    pdf_url         VARCHAR(500),
    line_items      JSONB NOT NULL DEFAULT '[]',
    issued_at       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    due_at          TIMESTAMPTZ,
    paid_at         TIMESTAMPTZ,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE subscription.usage_records (
    id              UUID PRIMARY KEY DEFAULT uuid_generate_v7(),
    subscription_id UUID NOT NULL REFERENCES subscription.subscriptions(id) ON DELETE RESTRICT,
    user_id         UUID NOT NULL,
    translation_room_id      UUID,
    usage_type      VARCHAR(30) NOT NULL
                    CHECK (usage_type IN ('stt_minutes','translation_chunks','tts_minutes','voice_clone_minutes','ai_summary')),
    credits_consumed INT NOT NULL CHECK (credits_consumed >= 0),
    duration_seconds INT CHECK (duration_seconds >= 0),
    details         VARCHAR(500),
    recorded_at     TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Indexes for Subscription
CREATE INDEX idx_subscriptions_user ON subscription.subscriptions(user_id);
CREATE INDEX idx_subscriptions_plan ON subscription.subscriptions(plan_id);
CREATE INDEX idx_subscriptions_status ON subscription.subscriptions(status) WHERE deleted_at IS NULL;
CREATE INDEX idx_subscriptions_active_expiring ON subscription.subscriptions(status, current_period_end) WHERE status = 'active';
CREATE INDEX idx_credit_tx_subscription ON subscription.credit_transactions(subscription_id);
CREATE INDEX idx_credit_tx_user ON subscription.credit_transactions(user_id);
CREATE INDEX idx_payments_subscription ON subscription.payments(subscription_id);
CREATE INDEX idx_payments_user ON subscription.payments(user_id);
CREATE INDEX idx_payments_status ON subscription.payments(status);
CREATE INDEX idx_payments_provider_tx ON subscription.payments(provider_transaction_id);
CREATE INDEX idx_invoices_payment ON subscription.invoices(payment_id);
CREATE INDEX idx_invoices_user ON subscription.invoices(user_id);
CREATE INDEX idx_usage_subscription ON subscription.usage_records(subscription_id);
CREATE INDEX idx_usage_translation_room ON subscription.usage_records(translation_room_id);

-- Grants
GRANT USAGE ON SCHEMA subscription TO sub_svc;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA subscription TO sub_svc;
ALTER DEFAULT PRIVILEGES IN SCHEMA subscription GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO sub_svc;


-- ============================================
-- 6. NOTIFICATION SCHEMA
-- ============================================
CREATE SCHEMA IF NOT EXISTS notification;

CREATE TABLE notification.notification_templates (
    id            UUID PRIMARY KEY DEFAULT uuid_generate_v7(),
    type          VARCHAR(50) UNIQUE NOT NULL,
    channel       VARCHAR(20) NOT NULL CHECK (channel IN ('email','push','in_app','sms')),
    subject       VARCHAR(255),
    body_template TEXT NOT NULL,
    variables     JSONB NOT NULL DEFAULT '[]',
    is_active     BOOLEAN NOT NULL DEFAULT true,
    created_at    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at    TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE notification.notifications (
    id           UUID NOT NULL DEFAULT uuid_generate_v7(),
    user_id      UUID NOT NULL,
    workspace_id UUID,
    type         VARCHAR(50) NOT NULL,
    channel      VARCHAR(20) NOT NULL CHECK (channel IN ('email','push','in_app','sms')),
    title        VARCHAR(255) NOT NULL,
    body         TEXT NOT NULL,
    data         JSONB,
    priority     VARCHAR(10) NOT NULL DEFAULT 'normal'
                 CHECK (priority IN ('low','normal','high','urgent')),
    status       VARCHAR(20) NOT NULL DEFAULT 'pending'
                 CHECK (status IN ('pending','sent','delivered','failed','cancelled')),
    scheduled_at TIMESTAMPTZ,
    sent_at      TIMESTAMPTZ,
    read_at      TIMESTAMPTZ,
    created_at   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (id, created_at)
) PARTITION BY RANGE (created_at);

CREATE TABLE notification.notifications_y2025 PARTITION OF notification.notifications
    FOR VALUES FROM ('2025-01-01') TO ('2026-01-01');
CREATE TABLE notification.notifications_y2026 PARTITION OF notification.notifications
    FOR VALUES FROM ('2026-01-01') TO ('2027-01-01');
CREATE TABLE notification.notifications_y2027 PARTITION OF notification.notifications
    FOR VALUES FROM ('2027-01-01') TO ('2028-01-01');
CREATE TABLE notification.notifications_default PARTITION OF notification.notifications DEFAULT;

CREATE TABLE notification.email_delivery_logs (
    id                  UUID NOT NULL DEFAULT uuid_generate_v7(),
    notification_id     UUID NOT NULL,
    to_email            VARCHAR(320) NOT NULL,
    subject             VARCHAR(255) NOT NULL,
    provider            VARCHAR(30) NOT NULL,
    provider_message_id VARCHAR(255),
    status              VARCHAR(20) NOT NULL DEFAULT 'queued'
                        CHECK (status IN ('queued','sent','delivered','opened','bounced','failed')),
    failure_reason      VARCHAR(500),
    sent_at             TIMESTAMPTZ,
    delivered_at        TIMESTAMPTZ,
    opened_at           TIMESTAMPTZ,
    bounced_at          TIMESTAMPTZ,
    PRIMARY KEY (id, sent_at)
) PARTITION BY RANGE (sent_at);

CREATE TABLE notification.email_delivery_logs_y2025 PARTITION OF notification.email_delivery_logs
    FOR VALUES FROM ('2025-01-01') TO ('2026-01-01');
CREATE TABLE notification.email_delivery_logs_y2026 PARTITION OF notification.email_delivery_logs
    FOR VALUES FROM ('2026-01-01') TO ('2027-01-01');
CREATE TABLE notification.email_delivery_logs_y2027 PARTITION OF notification.email_delivery_logs
    FOR VALUES FROM ('2027-01-01') TO ('2028-01-01');
CREATE TABLE notification.email_delivery_logs_default PARTITION OF notification.email_delivery_logs DEFAULT;

CREATE TABLE notification.push_subscriptions (
    id           UUID PRIMARY KEY DEFAULT uuid_generate_v7(),
    user_id      UUID NOT NULL,
    device_token VARCHAR(500) UNIQUE NOT NULL,
    platform     VARCHAR(20) NOT NULL CHECK (platform IN ('web','macos','windows','ios','android')),
    device_name  VARCHAR(100),
    is_active    BOOLEAN NOT NULL DEFAULT true,
    last_used_at TIMESTAMPTZ,
    created_at   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at   TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE notification.notification_preferences (
    id                UUID PRIMARY KEY DEFAULT uuid_generate_v7(),
    user_id           UUID NOT NULL,
    notification_type VARCHAR(50) NOT NULL,
    email_enabled     BOOLEAN NOT NULL DEFAULT true,
    push_enabled      BOOLEAN NOT NULL DEFAULT true,
    in_app_enabled    BOOLEAN NOT NULL DEFAULT true,
    updated_at        TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE (user_id, notification_type)
);

-- Indexes for Notification
CREATE INDEX idx_notifications_user ON notification.notifications(user_id);
CREATE INDEX idx_notifications_type ON notification.notifications(type);
CREATE INDEX idx_notifications_status ON notification.notifications(status);
CREATE INDEX idx_notifications_user_unread ON notification.notifications(user_id, status) WHERE read_at IS NULL;
CREATE INDEX idx_email_logs_notification ON notification.email_delivery_logs(notification_id);
CREATE INDEX idx_push_subs_user ON notification.push_subscriptions(user_id);
CREATE INDEX idx_notif_prefs_user ON notification.notification_preferences(user_id);

-- Grants
GRANT USAGE ON SCHEMA notification TO notif_svc;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA notification TO notif_svc;
ALTER DEFAULT PRIVILEGES IN SCHEMA notification GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO notif_svc;

-- End of script
