-- Migration: 016-add-billing-policy-config
-- Description:
--   Stores admin-editable invoice policy values and backfills the remaining
--   pricing guardrail keys used by BillingService plan/contract validation.

CREATE TABLE IF NOT EXISTS subscription.billing_policy_config (
    key varchar(100) PRIMARY KEY,
    value numeric(18, 6) NOT NULL,
    updated_at timestamptz NOT NULL DEFAULT NOW()
);

INSERT INTO subscription.billing_policy_config (key, value)
VALUES
    ('vat_rate', 0.10)
ON CONFLICT (key) DO NOTHING;

INSERT INTO subscription.billing_pricing_config (key, value)
VALUES
    ('minimum_price_per_credit_vnd', 2.60),
    ('minimum_contract_price_vnd', 15000),
    ('minimum_contract_price_usd', 0.50),
    ('sales_usage_weight', 0.45),
    ('sales_members_weight', 0.15),
    ('sales_languages_weight', 0.15),
    ('sales_ai_services_weight', 0.25),
    ('default_overage_cap_ratio', 0.15),
    ('default_invoice_terms_days', 15),
    ('default_invoice_grace_hours', 360)
ON CONFLICT (key) DO NOTHING;
