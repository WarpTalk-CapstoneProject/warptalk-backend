-- Migration: 038-26-07-2026-add-phase2-billing-rate-card-model-pricing
-- Description:
--   Extends subscription.usage_rate_card from the initial language/currency shape
--   into the Phase 2 provider/model/unit rate card used by the AI billing worker.
--   Keeps the migration idempotent because earlier environments may already have
--   some credit transaction audit columns from migration 036.


ALTER TABLE subscription.usage_rate_card
    ADD COLUMN IF NOT EXISTS unit varchar(30),
    ADD COLUMN IF NOT EXISTS provider varchar(50),
    ADD COLUMN IF NOT EXISTS model varchar(100),
    ADD COLUMN IF NOT EXISTS provider_unit_cost numeric(18, 10),
    ADD COLUMN IF NOT EXISTS markup_multiplier numeric(10, 4),
    ADD COLUMN IF NOT EXISTS is_active boolean NOT NULL DEFAULT true,
    ADD COLUMN IF NOT EXISTS notes text;

ALTER TABLE subscription.usage_rate_card
    ALTER COLUMN unit_price TYPE numeric(18, 6);

ALTER TABLE subscription.credit_transactions
    ADD COLUMN IF NOT EXISTS pricing_rate_card_id uuid,
    ADD COLUMN IF NOT EXISTS unit_price_snapshot numeric(18, 6);

ALTER TABLE subscription.credit_transactions
    ALTER COLUMN unit_price_snapshot TYPE numeric(18, 6);

ALTER TABLE subscription.usage_records
    ALTER COLUMN user_id DROP NOT NULL;

DROP INDEX IF EXISTS subscription.usage_rate_card_lookup_idx;
DROP INDEX IF EXISTS subscription.usage_rate_card_one_active_per_combo_idx;
DROP INDEX IF EXISTS subscription.ux_usage_rate_card_active_lookup;

CREATE UNIQUE INDEX IF NOT EXISTS ux_usage_rate_card_active_lookup
ON subscription.usage_rate_card (
    charge_type,
    unit,
    currency,
    provider,
    model,
    COALESCE(source_language_code, ''),
    COALESCE(target_language_code, '')
)
WHERE is_active = true AND effective_to IS NULL;

CREATE INDEX IF NOT EXISTS ix_usage_rate_card_resolver_lookup
ON subscription.usage_rate_card (
    charge_type,
    unit,
    currency,
    provider,
    model,
    effective_from DESC
)
WHERE is_active = true;

CREATE INDEX IF NOT EXISTS ix_credit_transactions_pricing_rate_card_id
ON subscription.credit_transactions (pricing_rate_card_id);

DO $$
BEGIN
    IF to_regclass('subscription.ux_credit_transactions_idempotency_key') IS NULL THEN
        IF to_regclass('subscription.credit_transactions_idempotency_key_idx') IS NOT NULL THEN
            EXECUTE 'ALTER INDEX subscription.credit_transactions_idempotency_key_idx RENAME TO ux_credit_transactions_idempotency_key';
        ELSE
            EXECUTE 'CREATE UNIQUE INDEX ux_credit_transactions_idempotency_key
                     ON subscription.credit_transactions (idempotency_key)
                     WHERE idempotency_key IS NOT NULL';
        END IF;
    END IF;
END $$;

