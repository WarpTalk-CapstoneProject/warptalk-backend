-- Migration: 20260924150000_add_fx_rates_from_stripe
-- Description:
--   The USD→VND rate every VND report converts with now comes from Stripe, with a daily history.
--
--   WHY
--     billing_pricing_config.fx_rate_usd_vnd was a hand-typed 26,300 that nothing ever updated, and
--     every past period was converted at whatever it said today. FxRateRefreshWorker (billing) now
--     records Stripe's rate once per UTC day:
--       stripe_fx_quote   POST /v1/fx_quotes (preview API), usd→vnd, the fee-exclusive base_rate;
--       stripe_charge     the rate Stripe applied converting a VND charge into the USD balance
--                         (balance_transaction.exchange_rate), which also backfills past days;
--       manual            an admin's explicit override for that day (/admin/settings).
--     Reports convert each day at that day's rate; a day with none uses the last known one and
--     says so. fx_rate_usd_vnd stays, kept equal to today's effective rate, for the readers that
--     take one number (rate-card pricing preview, the Insights snapshot).
--
--   1. subscription.fx_rates — one row per (base, quote, UTC day, source).
--   2. fx_rate_usd_vnd_manual = 0: Stripe is the default; 1 means the admin overrides it.
--
--   Idempotent: IF NOT EXISTS / ON CONFLICT DO NOTHING. No BEGIN/COMMIT — the migration runner owns
--   the transaction.

CREATE TABLE IF NOT EXISTS subscription.fx_rates (
    id                 uuid           NOT NULL DEFAULT gen_random_uuid(),
    base_currency      varchar(3)     NOT NULL,
    quote_currency     varchar(3)     NOT NULL,
    rate_date          date           NOT NULL,
    rate               numeric(24,10) NOT NULL,
    source             varchar(40)    NOT NULL,
    fee_inclusive_rate numeric(24,10) NULL,
    source_ref         varchar(200)   NULL,
    fetched_at         timestamptz    NOT NULL DEFAULT NOW(),
    CONSTRAINT fx_rates_pkey PRIMARY KEY (id),
    CONSTRAINT ck_fx_rates_rate CHECK (rate > 0),
    CONSTRAINT ck_fx_rates_source CHECK (source IN ('stripe_fx_quote', 'stripe_charge', 'manual'))
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_fx_rates_pair_day_source
    ON subscription.fx_rates (base_currency, quote_currency, rate_date, source);

COMMENT ON TABLE subscription.fx_rates IS
    'Exchange rates per UTC day and source (Stripe FX quote, Stripe charge conversion, manual override). Written by FxRateRefreshWorker and /admin/billing/fx; read by every VND report.';
COMMENT ON COLUMN subscription.fx_rates.rate IS
    'QUOTE per 1 BASE, fee-exclusive (USD→VND: VND per US dollar).';
COMMENT ON COLUMN subscription.fx_rates.fee_inclusive_rate IS
    'The rate after Stripe''s FX fee (fx_quote exchange_rate), when the source reports one.';

DO $grant$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'warptalk_billing_runtime') THEN
        GRANT SELECT, INSERT, UPDATE, DELETE ON subscription.fx_rates TO warptalk_billing_runtime;
    END IF;
END
$grant$;

INSERT INTO subscription.billing_pricing_config (key, value)
VALUES ('fx_rate_usd_vnd_manual', 0)
ON CONFLICT (key) DO NOTHING;
