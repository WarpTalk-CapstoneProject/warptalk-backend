-- Migration: 040-26-07-2026-seed-enterprise-subscription-plan
-- Description:
--   Implements Phase 2 P2.8 from credit-billing-master-plan.md.
--   Adds the contract/default plan columns required by M2, disables legacy
--   active plans, and upserts the single Enterprise plan template.
--
-- CUSTOMER-VISIBLE PRICING CHANGE — this is not a schema-only migration.
--   Confirmed intended (Phase 2 moves to a single contract-based Enterprise plan).
--   Two effects land on whatever database this runs against:
--
--   1. Every active plan whose slug is not 'enterprise' is set is_active = false.
--      On production that retires "Startup" (190,000 VND), which disappears from
--      /workspace/payment/plans. Rows are kept, not deleted, so historical
--      subscriptions still resolve their plan.
--   2. The Enterprise plan is upserted at price = 1,900,000 VND with
--      credits_per_cycle = 700,000. Production currently carries Enterprise at
--      490,000 VND, so this is roughly a 4x change to a live price.
--
--   Per-customer numbers are not meant to be edited here: contract-specific
--   values belong in the subscription.subscriptions *_override columns added by
--   this migration and by 041.


ALTER TABLE subscription.plans
    ADD COLUMN IF NOT EXISTS overage_cap_credits int NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS overage_price_per_credit numeric(12, 4) NOT NULL DEFAULT 4.0000,
    ADD COLUMN IF NOT EXISTS low_balance_threshold_credits int NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS rollover_cap_credits int NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS invoice_terms_days int NOT NULL DEFAULT 15,
    ADD COLUMN IF NOT EXISTS invoice_grace_hours int NOT NULL DEFAULT 360;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'plans_warn_before_overage_chk'
          AND conrelid = 'subscription.plans'::regclass
    ) THEN
        ALTER TABLE subscription.plans
            ADD CONSTRAINT plans_warn_before_overage_chk
            CHECK (low_balance_threshold_credits > overage_cap_credits OR overage_cap_credits = 0);
    END IF;
END $$;

ALTER TABLE subscription.subscriptions
    ADD COLUMN IF NOT EXISTS credits_per_cycle_override int,
    ADD COLUMN IF NOT EXISTS contract_price_vnd numeric(14, 2),
    ADD COLUMN IF NOT EXISTS overage_cap_credits_override int,
    ADD COLUMN IF NOT EXISTS overage_price_per_credit_override numeric(12, 4),
    ADD COLUMN IF NOT EXISTS invoice_terms_days_override int,
    ADD COLUMN IF NOT EXISTS billing_contact_email varchar(255),
    ADD COLUMN IF NOT EXISTS overage_credits_this_cycle int NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS overage_started_at timestamptz,
    ADD COLUMN IF NOT EXISTS service_state varchar(20) NOT NULL DEFAULT 'healthy',
    ADD COLUMN IF NOT EXISTS suspended_reason varchar(30);

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'subscriptions_price_floor_chk'
          AND conrelid = 'subscription.subscriptions'::regclass
    ) THEN
        ALTER TABLE subscription.subscriptions
            ADD CONSTRAINT subscriptions_price_floor_chk
            CHECK (
                contract_price_vnd IS NULL
                OR credits_per_cycle_override IS NULL
                OR credits_per_cycle_override = 0
                OR contract_price_vnd / credits_per_cycle_override >= 2.60
            );
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS ix_subscriptions_service_state
ON subscription.subscriptions (service_state)
WHERE deleted_at IS NULL AND service_state <> 'healthy';

DO $$
BEGIN
    IF to_regclass('subscription.invoices') IS NOT NULL
       AND EXISTS (
           SELECT 1
           FROM information_schema.columns
           WHERE table_schema = 'subscription'
             AND table_name = 'invoices'
             AND column_name = 'due_at'
       )
       AND to_regclass('subscription.ix_invoices_overdue') IS NULL THEN
        EXECUTE 'CREATE INDEX ix_invoices_overdue
                 ON subscription.invoices (due_at)
                 WHERE status <> ''paid''';
    END IF;
END $$;

-- Keep historical subscriptions valid by not deleting old plan rows.
-- Phase 2 uses one active Enterprise template; contract-specific numbers live
-- on subscription.subscriptions *_override columns.
UPDATE subscription.plans
SET
    is_active = false,
    updated_at = now()
WHERE slug <> 'enterprise'
  AND is_active = true;

INSERT INTO subscription.plans (
    id,
    name,
    slug,
    tier,
    price,
    currency,
    billing_cycle,
    credits_per_cycle,
    max_participants,
    max_languages,
    voice_clone_enabled,
    ai_assistant_enabled,
    glossary_enabled,
    dedicated_gpu,
    features,
    sort_order,
    is_active,
    overage_cap_credits,
    overage_price_per_credit,
    low_balance_threshold_credits,
    rollover_cap_credits,
    invoice_terms_days,
    invoice_grace_hours,
    created_at,
    updated_at
)
VALUES (
    uuidv7(),
    'Enterprise',
    'enterprise',
    'Enterprise',
    1900000.00,
    'VND',
    'monthly',
    700000,
    500,
    3,
    true,
    true,
    true,
    true,
    '{
        "voice_clone_limit_mins": -1,
        "billing_model": "contract_template",
        "overage_policy": "invoice_after_cap",
        "external_integrations": {
            "google_meet": true
        },
        "supported_external_platforms": [
            "google_meet"
        ]
    }'::jsonb,
    1,
    true,
    105000,
    4.0000,
    140000,
    700000,
    15,
    360,
    now(),
    now()
)
ON CONFLICT (slug) DO UPDATE SET
    name = EXCLUDED.name,
    tier = EXCLUDED.tier,
    price = EXCLUDED.price,
    currency = EXCLUDED.currency,
    billing_cycle = EXCLUDED.billing_cycle,
    credits_per_cycle = EXCLUDED.credits_per_cycle,
    max_participants = EXCLUDED.max_participants,
    max_languages = EXCLUDED.max_languages,
    voice_clone_enabled = EXCLUDED.voice_clone_enabled,
    ai_assistant_enabled = EXCLUDED.ai_assistant_enabled,
    glossary_enabled = EXCLUDED.glossary_enabled,
    dedicated_gpu = EXCLUDED.dedicated_gpu,
    features = subscription.plans.features || EXCLUDED.features,
    sort_order = EXCLUDED.sort_order,
    is_active = true,
    overage_cap_credits = EXCLUDED.overage_cap_credits,
    overage_price_per_credit = EXCLUDED.overage_price_per_credit,
    low_balance_threshold_credits = EXCLUDED.low_balance_threshold_credits,
    rollover_cap_credits = EXCLUDED.rollover_cap_credits,
    invoice_terms_days = EXCLUDED.invoice_terms_days,
    invoice_grace_hours = EXCLUDED.invoice_grace_hours,
    updated_at = now();

