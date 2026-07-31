-- Migration: 036-25-07-2026-align-billing-entities-with-subscription-schema
-- Description:
--   Keeps the subscription schema aligned with WarpTalk.BillingService entities after
--   realtime credit settlement introduced rate cards and ledger audit columns.
--   This migration is intentionally idempotent because some environments already got
--   parts of this shape via migrations 017, 019, 022, and 023.


CREATE TABLE IF NOT EXISTS subscription.usage_rate_card (
  id UUID PRIMARY KEY DEFAULT (uuidv7()),
  charge_type VARCHAR(30) NOT NULL,
  source_language_code VARCHAR(15),
  target_language_code VARCHAR(15),
  unit_price DECIMAL(12,6) NOT NULL,
  currency CHAR(3) NOT NULL,
  effective_from TIMESTAMPTZ NOT NULL,
  effective_to TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS usage_rate_card_lookup_idx
  ON subscription.usage_rate_card (charge_type, currency, source_language_code, target_language_code, effective_from);

CREATE UNIQUE INDEX IF NOT EXISTS usage_rate_card_one_active_per_combo_idx
  ON subscription.usage_rate_card (charge_type, currency, source_language_code, target_language_code)
  WHERE effective_to IS NULL;

ALTER TABLE subscription.credit_transactions
  ADD COLUMN IF NOT EXISTS workspace_id UUID,
  ADD COLUMN IF NOT EXISTS charge_type VARCHAR(30),
  ADD COLUMN IF NOT EXISTS pricing_rate_card_id UUID,
  ADD COLUMN IF NOT EXISTS usage_record_id UUID,
  ADD COLUMN IF NOT EXISTS unit_price_snapshot DECIMAL(12,6),
  ADD COLUMN IF NOT EXISTS invoice_id UUID,
  ADD COLUMN IF NOT EXISTS reversal_of_transaction_id UUID,
  ADD COLUMN IF NOT EXISTS currency CHAR(3),
  ADD COLUMN IF NOT EXISTS idempotency_key VARCHAR(255),
  ADD COLUMN IF NOT EXISTS triggered_by_participant_id UUID,
  ADD COLUMN IF NOT EXISTS transcript_segment_id UUID;

COMMENT ON COLUMN subscription.credit_transactions.workspace_id IS 'External AuthService workspace id. No physical FK.';
COMMENT ON COLUMN subscription.credit_transactions.triggered_by_participant_id IS 'External TranslationRoomService participant id. No physical FK.';
COMMENT ON COLUMN subscription.credit_transactions.transcript_segment_id IS 'External TranscriptService transcript_segments.id. No physical FK.';

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'credit_transactions_pricing_rate_card_id_fkey') THEN
        ALTER TABLE subscription.credit_transactions
          ADD CONSTRAINT credit_transactions_pricing_rate_card_id_fkey
          FOREIGN KEY (pricing_rate_card_id) REFERENCES subscription.usage_rate_card (id)
          DEFERRABLE INITIALLY IMMEDIATE;
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'credit_transactions_usage_record_id_fkey') THEN
        ALTER TABLE subscription.credit_transactions
          ADD CONSTRAINT credit_transactions_usage_record_id_fkey
          FOREIGN KEY (usage_record_id) REFERENCES subscription.usage_records (id)
          DEFERRABLE INITIALLY IMMEDIATE;
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'credit_transactions_invoice_id_fkey') THEN
        ALTER TABLE subscription.credit_transactions
          ADD CONSTRAINT credit_transactions_invoice_id_fkey
          FOREIGN KEY (invoice_id) REFERENCES subscription.invoices (id)
          DEFERRABLE INITIALLY IMMEDIATE;
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'credit_transactions_reversal_of_transaction_id_fkey') THEN
        ALTER TABLE subscription.credit_transactions
          ADD CONSTRAINT credit_transactions_reversal_of_transaction_id_fkey
          FOREIGN KEY (reversal_of_transaction_id) REFERENCES subscription.credit_transactions (id)
          DEFERRABLE INITIALLY IMMEDIATE;
    END IF;
END $$;

CREATE UNIQUE INDEX IF NOT EXISTS credit_transactions_idempotency_key_idx
  ON subscription.credit_transactions (idempotency_key)
  WHERE idempotency_key IS NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS credit_transactions_reversal_unique_idx
  ON subscription.credit_transactions (reversal_of_transaction_id)
  WHERE reversal_of_transaction_id IS NOT NULL;

CREATE INDEX IF NOT EXISTS credit_transactions_transcript_segment_id_idx
  ON subscription.credit_transactions (transcript_segment_id)
  WHERE transcript_segment_id IS NOT NULL;

