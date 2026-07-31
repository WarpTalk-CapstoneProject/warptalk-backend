-- Migration: 042-27-07-2026-add-billing-pricing-config
-- Description:
--   Stores admin-editable billing pricing parameters used by the rate-card
--   draft UI. Usage settlement still resolves immutable usage_rate_card rows.


CREATE TABLE IF NOT EXISTS subscription.billing_pricing_config (
    key varchar(80) PRIMARY KEY,
    value numeric(18, 6) NOT NULL,
    updated_at timestamptz NOT NULL DEFAULT NOW()
);

INSERT INTO subscription.billing_pricing_config (key, value)
VALUES
    ('fx_rate_usd_vnd', 26300),
    ('credit_value_vnd', 4)
ON CONFLICT (key) DO NOTHING;

