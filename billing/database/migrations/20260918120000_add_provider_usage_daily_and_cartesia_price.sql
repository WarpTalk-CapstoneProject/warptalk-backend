-- Migration: 20260918120000_add_provider_usage_daily_and_cartesia_price
-- Description:
--   Measured Cartesia usage for admin Insights, and the price of a Cartesia credit.
--
--   WHY
--     The AI provider cost of dubbing (AUDIO_DUBBING_STANDARD / AUDIO_DUBBING_VOICE_CLONE) was
--     derived from an ASSUMED 12.5 characters per second of synthesised audio
--     (20260918090000_set_provider_cost_on_crd_rate_cards.sql). Cartesia reports what it actually
--     charged — credits per UTC day, by capability and by model — through GET /usage/credits with an
--     admin API key. CartesiaUsageSyncWorker (billing) copies that into provider_usage_daily every few
--     minutes, and Insights prices dubbing from it: credits × cartesia_usd_per_credit × fx_rate_usd_vnd.
--
--   1. subscription.provider_usage_daily — one row per (provider, UTC day, group kind, group id).
--      group_kind 'total' (group_id 'all') is written for every synced day even at 0 credits, so it
--      doubles as the marker that the day was synced at all.
--   2. billing_pricing_config.value widened from numeric(18,6) to numeric(24,10): the Cartesia price
--      below has 7 decimals, and (18,6) would silently store 0.000039 — 0.5% off every measured cost.
--      Widening a numeric keeps every existing value exactly; no view depends on the column.
--   3. cartesia_usd_per_credit = 0.0000392, the Startup plan's $49 / 1,250,000 credits. The account is
--      on the Free plan spending credits bought earlier, so the marginal price is 0, but margin
--      reporting charges what those credits cost. Admins edit it in /admin/settings.
--
--   Idempotent: IF NOT EXISTS / ON CONFLICT DO NOTHING, and re-running the ALTER is a no-op. No
--   BEGIN/COMMIT — the migration runner owns the transaction.

CREATE TABLE IF NOT EXISTS subscription.provider_usage_daily (
    id          uuid         NOT NULL DEFAULT gen_random_uuid(),
    provider    varchar(40)  NOT NULL,
    usage_date  date         NOT NULL,
    group_kind  varchar(20)  NOT NULL,
    group_id    varchar(200) NOT NULL,
    group_label varchar(300) NULL,
    credits     bigint       NOT NULL DEFAULT 0,
    synced_at   timestamptz  NOT NULL DEFAULT NOW(),
    CONSTRAINT provider_usage_daily_pkey PRIMARY KEY (id),
    CONSTRAINT ck_provider_usage_daily_group_kind CHECK (group_kind IN ('total', 'capability', 'model')),
    CONSTRAINT ck_provider_usage_daily_credits CHECK (credits >= 0)
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_provider_usage_daily_day_group
    ON subscription.provider_usage_daily (provider, usage_date, group_kind, group_id);

COMMENT ON TABLE subscription.provider_usage_daily IS
    'Usage measured by an AI provider''s own usage API, per UTC day (Cartesia GET /usage/credits). Written by CartesiaUsageSyncWorker; read by admin Insights for the provider cost of dubbing.';
COMMENT ON COLUMN subscription.provider_usage_daily.usage_date IS
    'The provider''s calendar day. Cartesia buckets usage by UTC day.';
COMMENT ON COLUMN subscription.provider_usage_daily.group_kind IS
    'total (group_id all, one row per synced day even at 0 credits) | capability | model.';
COMMENT ON COLUMN subscription.provider_usage_daily.credits IS
    'Provider credits consumed that day in this group (Cartesia: ~1 credit per TTS character).';

-- The runner's default privileges already grant this to the runtime role; stated explicitly, as
-- 20260806090000 does, so the grant does not depend on which role ran the file. Guarded because a
-- scratch database (tests, local) has no such role.
DO $grant$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'warptalk_billing_runtime') THEN
        GRANT SELECT, INSERT, UPDATE, DELETE ON subscription.provider_usage_daily TO warptalk_billing_runtime;
    END IF;
END
$grant$;

ALTER TABLE subscription.billing_pricing_config
    ALTER COLUMN value TYPE numeric(24, 10);

INSERT INTO subscription.billing_pricing_config (key, value)
VALUES ('cartesia_usd_per_credit', 0.0000392)
ON CONFLICT (key) DO NOTHING;
