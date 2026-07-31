-- Migration: 046-29-07-2026-widen-payment-provider-identifiers
-- Description:
--   Allows local mock checkout session IDs to be stored with their encoded
--   metadata payload. Real Stripe ids remain much shorter, but the localhost
--   fallback deliberately carries enough data for payment success verification.


ALTER TABLE subscription.payments
    ALTER COLUMN provider_transaction_id TYPE varchar(1024),
    ALTER COLUMN provider_order_id TYPE varchar(1024);

