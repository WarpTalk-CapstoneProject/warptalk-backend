-- Migration: 20260925090000_add_provider_call_stats_and_status_incidents
-- Description:
--   The admin Providers page (OpenAI, Cartesia, LiveKit, Stripe): our own calls to each provider,
--   and the incidents each provider publishes on its status page, kept for the 90-day uptime row.
--
--   WHY
--     The AI workers now count every call they make to OpenAI and Cartesia — outcome (ok, 402 quota,
--     429 rate limit, 401/403, other 4xx, 5xx, timeout, network, unclassified) and latency — into a
--     Redis hash per UTC day (warptalk-ai shared/provider_calls.py) that lives 3 days. Prometheus keeps
--     7. The page needs 90, so ProviderCallStatsSyncWorker (billing) copies the hashes here every few
--     minutes. A counter only ever rises to the larger of stored and reported value, so a hash lost to
--     Redis eviction can undercount an hour but never erase it.
--
--     ProviderStatusPollWorker reads each configured public status page (statuspage.io v2 API:
--     LiveKit, OpenAI, Cartesia) and stores its incidents, because the page's own list only holds the
--     latest 25-50.
--
--   1. subscription.provider_call_stats — one row per (provider, UTC hour, operation, model).
--   2. subscription.provider_status_incidents — one row per (provider, the status page's incident id).
--
--   Idempotent: IF NOT EXISTS throughout. No BEGIN/COMMIT — the migration runner owns the transaction.

CREATE TABLE IF NOT EXISTS subscription.provider_call_stats (
    id              uuid          NOT NULL DEFAULT gen_random_uuid(),
    provider        varchar(40)   NOT NULL,
    hour_start      timestamptz   NOT NULL,
    operation       varchar(80)   NOT NULL,
    model           varchar(120)  NOT NULL DEFAULT '-',
    ok              bigint        NOT NULL DEFAULT 0,
    quota           bigint        NOT NULL DEFAULT 0,
    rate_limited    bigint        NOT NULL DEFAULT 0,
    auth            bigint        NOT NULL DEFAULT 0,
    client_error    bigint        NOT NULL DEFAULT 0,
    server_error    bigint        NOT NULL DEFAULT 0,
    timeout         bigint        NOT NULL DEFAULT 0,
    network_error   bigint        NOT NULL DEFAULT 0,
    error           bigint        NOT NULL DEFAULT 0,
    latency_count   bigint        NOT NULL DEFAULT 0,
    latency_sum_ms  bigint        NOT NULL DEFAULT 0,
    latency_buckets jsonb         NOT NULL DEFAULT '{}'::jsonb,
    synced_at       timestamptz   NOT NULL DEFAULT NOW(),
    CONSTRAINT provider_call_stats_pkey PRIMARY KEY (id),
    CONSTRAINT ck_provider_call_stats_counts CHECK (
        ok >= 0 AND quota >= 0 AND rate_limited >= 0 AND auth >= 0 AND client_error >= 0
        AND server_error >= 0 AND timeout >= 0 AND network_error >= 0 AND error >= 0
        AND latency_count >= 0 AND latency_sum_ms >= 0)
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_provider_call_stats_hour
    ON subscription.provider_call_stats (provider, hour_start, operation, model);

CREATE INDEX IF NOT EXISTS ix_provider_call_stats_hour
    ON subscription.provider_call_stats (hour_start);

COMMENT ON TABLE subscription.provider_call_stats IS
    'WarpTalk''s own calls to each external provider per UTC hour, operation and model (outcome counts and a latency histogram). Copied from the AI workers'' Redis hashes warptalk:provider_calls:{day} by ProviderCallStatsSyncWorker; read by the admin Providers page.';
COMMENT ON COLUMN subscription.provider_call_stats.client_error IS
    'Any 4xx other than 401/402/403/429: a request WarpTalk got wrong. Not counted against the provider''s availability.';
COMMENT ON COLUMN subscription.provider_call_stats.latency_buckets IS
    'Histogram {"100":n,"250":n,…,"+Inf":n} in ms, not cumulative. Time to response headers (HTTP) or to first audio (Cartesia websocket).';

CREATE TABLE IF NOT EXISTS subscription.provider_status_incidents (
    id           uuid          NOT NULL DEFAULT gen_random_uuid(),
    provider     varchar(40)   NOT NULL,
    external_id  varchar(100)  NOT NULL,
    name         varchar(500)  NOT NULL,
    impact       varchar(20)   NOT NULL,
    status       varchar(40)   NOT NULL,
    started_at   timestamptz   NOT NULL,
    resolved_at  timestamptz   NULL,
    url          varchar(500)  NULL,
    synced_at    timestamptz   NOT NULL DEFAULT NOW(),
    CONSTRAINT provider_status_incidents_pkey PRIMARY KEY (id)
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_provider_status_incidents_external
    ON subscription.provider_status_incidents (provider, external_id);

CREATE INDEX IF NOT EXISTS ix_provider_status_incidents_started
    ON subscription.provider_status_incidents (provider, started_at);

COMMENT ON TABLE subscription.provider_status_incidents IS
    'Incidents a provider published on its own status page (statuspage.io v2 /api/v2/incidents.json), stored by ProviderStatusPollWorker for the admin Providers page''s 90-day uptime row.';

-- Stated explicitly, as 20260918120000 does, so the grant does not depend on which role ran the file.
-- Guarded because a scratch database (tests, local) has no such role.
DO $grant$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'warptalk_billing_runtime') THEN
        GRANT SELECT, INSERT, UPDATE, DELETE ON subscription.provider_call_stats TO warptalk_billing_runtime;
        GRANT SELECT, INSERT, UPDATE, DELETE ON subscription.provider_status_incidents TO warptalk_billing_runtime;
    END IF;
END
$grant$;
