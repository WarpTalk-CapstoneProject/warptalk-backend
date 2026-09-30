-- Migration: 20260925200000_add_stripe_call_outcomes_and_payment_fees
-- Description:
--   Make the Stripe card of the admin Providers page real (owner, 2026-09-25: "Stripe status
--   doesn't work, no data").
--
--   1. subscription.provider_call_stats.declined — a card the issuer declined (Stripe answers 402
--      card_error). Stripe worked; the customer's card did not. Counted apart from every failure
--      class so it can never pull Stripe's success rate down, and apart from `quota`, which is what a
--      402 from OpenAI or Cartesia means.
--   2. subscription.payment_provider_fees — for each paid Stripe payment, the balance transaction
--      of its charge: the fee Stripe kept, the net, the settlement currency and the exchange rate.
--      Written by StripeFeeSyncWorker (bounded, idempotent, 90-day backfill); read by the Providers
--      page as Stripe's cost. One row per payment; a payment Stripe has no charge for is recorded
--      with status not_found so it is not asked again, a transient failure as error and retried.
--
--   Idempotent: IF NOT EXISTS throughout. No BEGIN/COMMIT — the migration runner owns the transaction.

ALTER TABLE subscription.provider_call_stats
    ADD COLUMN IF NOT EXISTS declined bigint NOT NULL DEFAULT 0;

COMMENT ON COLUMN subscription.provider_call_stats.declined IS
    'Stripe 402 card_error: the issuer declined the card. Stripe answered correctly, so this is neither a success nor a provider failure in the availability maths.';

CREATE TABLE IF NOT EXISTS subscription.payment_provider_fees (
    id                     uuid           NOT NULL DEFAULT gen_random_uuid(),
    payment_id             uuid           NOT NULL,
    provider               varchar(40)    NOT NULL,
    status                 varchar(20)    NOT NULL,
    balance_transaction_id varchar(255)   NULL,
    charge_id              varchar(255)   NULL,
    currency               varchar(3)     NULL,
    amount                 numeric(18, 4) NULL,
    fee                    numeric(18, 4) NULL,
    net                    numeric(18, 4) NULL,
    exchange_rate          numeric(24, 10) NULL,
    occurred_at            timestamptz    NULL,
    error                  varchar(500)   NULL,
    fetched_at             timestamptz    NOT NULL DEFAULT NOW(),
    CONSTRAINT payment_provider_fees_pkey PRIMARY KEY (id),
    CONSTRAINT ck_payment_provider_fees_status CHECK (status IN ('ok', 'not_found', 'error'))
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_payment_provider_fees_payment
    ON subscription.payment_provider_fees (payment_id);

CREATE INDEX IF NOT EXISTS ix_payment_provider_fees_provider_occurred
    ON subscription.payment_provider_fees (provider, occurred_at);

COMMENT ON TABLE subscription.payment_provider_fees IS
    'Per paid payment, the processing fee the provider kept (Stripe balance transaction). Amounts are in major units of `currency` (the settlement currency). Read by the admin Providers page.';

DO $grant$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'warptalk_billing_runtime') THEN
        GRANT SELECT, INSERT, UPDATE, DELETE ON subscription.payment_provider_fees TO warptalk_billing_runtime;
    END IF;
END
$grant$;
