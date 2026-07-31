-- Migration: 043-28-07-2026-align-credit-transaction-history-schema
-- Description:
--   Align subscription.credit_transactions with the billing service entity used
--   by credit history endpoints. This keeps admin/workspace billing history
--   screens working after Phase 5 billing alignment.


ALTER TABLE subscription.credit_transactions
    ADD COLUMN IF NOT EXISTS correlation_id varchar(100),
    ADD COLUMN IF NOT EXISTS status varchar(20) NOT NULL DEFAULT 'committed';

CREATE UNIQUE INDEX IF NOT EXISTS ix_credit_transactions_correlation_type
    ON subscription.credit_transactions (correlation_id, type)
    WHERE correlation_id IS NOT NULL;

