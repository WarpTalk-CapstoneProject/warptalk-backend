-- USD becomes the accounting currency (owner decision, 2026-10-02).
--
-- Until now every report, the credit value, the per-credit price floor and every contract price were
-- in VND, and USD provider costs were converted INTO VND to be compared with them. From here USD is the
-- unit everything is measured in. Providers already bill in USD, so the rate-card formula loses its
-- exchange rate altogether:
--
--     unit price (credits) = provider_unit_cost_usd * markup_multiplier / credit_value_usd
--
-- WHAT IS CONVERTED, and at which rate
--     Only the stored figures whose meaning is "an amount of the accounting currency": the credit value,
--     the per-credit price floor, contract prices, contract overage overrides and expense budgets. Each is divided by the newest
--     USD→VND rate on record, which is Stripe's (the same precedence FxRateTable uses: an admin's manual
--     override for that day, then Stripe's FX quote, then a Stripe charge conversion), falling back to
--     the configured fx_rate_usd_vnd. One rate for all of them, so they stay consistent with each other.
--
-- WHAT IS NOT
--     * plans.price. The owner sets the USD price list in the admin portal; a migration must not invent
--       one. A VND plan stays a VND plan, and Stripe keeps charging it in VND, until it is repriced.
--     * Historical payments, invoices and fees. They are what Stripe actually took, in the currency it
--       took them in. Reports convert them at the rate of their own day.
--     * credit_packs.price_vnd / addons.price_*_vnd. VND remains a currency an item may be SOLD in.
--     * fx_rate_usd_vnd and the fx_rates table: still needed to read VND amounts in USD.
--     * subscription.settle_usage_charge's COALESCE(p_currency, 'VND'). Both callers always pass a
--       currency (billing_worker passes 'CRD'), so the default is unreachable, and re-creating the
--       money function to change an unreachable literal is not a trade worth making.
--
-- Prod at the time of writing: no contract prices, no budgets, 13 VND-labelled rate cards (labels only:
-- their unit_price is credits; settlement reads the CRD cards).

CREATE TEMP TABLE usd_accounting_fx ON COMMIT DROP AS
SELECT COALESCE(
    (SELECT r.rate
       FROM subscription.fx_rates r
      WHERE r.base_currency = 'USD'
        AND r.quote_currency = 'VND'
        AND r.rate > 0
      ORDER BY r.rate_date DESC,
               CASE r.source
                   WHEN 'manual' THEN 0
                   WHEN 'stripe_fx_quote' THEN 1
                   WHEN 'stripe_charge' THEN 2
                   ELSE 3
               END,
               r.fetched_at DESC
      LIMIT 1),
    (SELECT c.value
       FROM subscription.billing_pricing_config c
      WHERE c.key = 'fx_rate_usd_vnd'
        AND c.value > 0),
    26300
) AS vnd_per_usd;

-- 1. Pricing config: the credit value and the per-credit floor, now in USD.
UPDATE subscription.billing_pricing_config c
   SET key = 'credit_value_usd',
       value = round(c.value / f.vnd_per_usd, 10),
       updated_at = now()
  FROM usd_accounting_fx f
 WHERE c.key = 'credit_value_vnd'
   AND NOT EXISTS (SELECT 1 FROM subscription.billing_pricing_config WHERE key = 'credit_value_usd');

UPDATE subscription.billing_pricing_config c
   SET key = 'minimum_price_per_credit_usd',
       value = round(c.value / f.vnd_per_usd, 10),
       updated_at = now()
  FROM usd_accounting_fx f
 WHERE c.key = 'minimum_price_per_credit_vnd'
   AND NOT EXISTS (SELECT 1 FROM subscription.billing_pricing_config WHERE key = 'minimum_price_per_credit_usd');

-- minimum_contract_price_usd already exists beside it; the VND minimum is Stripe's own floor for a VND
-- charge now, a constant in code, not a pricing decision.
DELETE FROM subscription.billing_pricing_config
 WHERE key IN ('credit_value_vnd', 'minimum_price_per_credit_vnd', 'minimum_contract_price_vnd');

-- 2. Per-credit prices. A USD credit is worth ~$0.00015; at numeric(12,4) the default overage price
-- would round to $0.0002, a third too high. Ten decimals, like billing_pricing_config.
ALTER TABLE subscription.plans
    ALTER COLUMN overage_price_per_credit TYPE numeric(18, 10),
    ALTER COLUMN overage_price_per_credit SET DEFAULT 0.0001520913;

ALTER TABLE subscription.subscriptions
    ALTER COLUMN overage_price_per_credit_override TYPE numeric(18, 10);

-- A contract's overage override was a contract term entered in VND per credit (the editor said so);
-- contract terms are USD now. plans.overage_price_per_credit is in the plan's own currency and stays.
UPDATE subscription.subscriptions s
   SET overage_price_per_credit_override = round(s.overage_price_per_credit_override / f.vnd_per_usd, 10)
  FROM usd_accounting_fx f
 WHERE s.overage_price_per_credit_override IS NOT NULL;

-- 3. Currencies nobody stated are USD now.
ALTER TABLE subscription.plans ALTER COLUMN currency SET DEFAULT 'USD';
ALTER TABLE subscription.payments ALTER COLUMN currency SET DEFAULT 'USD';
ALTER TABLE subscription.invoices ALTER COLUMN currency SET DEFAULT 'USD';
ALTER TABLE subscription.operating_expenses ALTER COLUMN currency SET DEFAULT 'USD';

-- 4. Contract prices. The price-floor CHECK hard-coded 2.60 VND per credit, a copy of a configurable
-- value that had already drifted from it once; SubscriptionService validates against the configured
-- floor, which is the one an admin can see and change. Dropped rather than re-created with a frozen
-- USD number that would drift the same way.
ALTER TABLE subscription.subscriptions DROP CONSTRAINT IF EXISTS subscriptions_price_floor_chk;

-- Renamed where it exists. A schema built from the EF model (tests, a fresh dev database) already has
-- contract_price_usd beside the VND column the older migrations added: the VND values are converted
-- into it and the VND column is dropped, so every database ends with the one column.
DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM information_schema.columns
         WHERE table_schema = 'subscription' AND table_name = 'subscriptions'
           AND column_name = 'contract_price_vnd'
    ) THEN
        IF NOT EXISTS (
            SELECT 1 FROM information_schema.columns
             WHERE table_schema = 'subscription' AND table_name = 'subscriptions'
               AND column_name = 'contract_price_usd'
        ) THEN
            ALTER TABLE subscription.subscriptions ADD COLUMN contract_price_usd numeric(14, 2);
        END IF;

        UPDATE subscription.subscriptions s
           SET contract_price_usd = round(s.contract_price_vnd / f.vnd_per_usd, 2)
          FROM usd_accounting_fx f
         WHERE s.contract_price_vnd IS NOT NULL
           AND s.contract_price_usd IS NULL;

        ALTER TABLE subscription.subscriptions DROP COLUMN contract_price_vnd;
    END IF;
END $$;

-- An output column cannot be renamed by CREATE OR REPLACE.
DROP FUNCTION IF EXISTS subscription.resolve_contract_terms(uuid);

CREATE FUNCTION subscription.resolve_contract_terms(p_subscription_id uuid)
RETURNS TABLE (
    subscription_id uuid,
    plan_id uuid,
    credits_per_cycle integer,
    contract_price_usd numeric,
    overage_cap_credits integer,
    overage_price_per_credit numeric,
    low_balance_threshold_credits integer,
    rollover_cap_credits integer,
    invoice_terms_days integer,
    invoice_grace_hours integer,
    billing_contact_email character varying
)
LANGUAGE sql
STABLE
AS $function$
    SELECT
        s.id,
        p.id,
        COALESCE(s.credits_per_cycle_override, p.credits_per_cycle),
        s.contract_price_usd,
        COALESCE(s.overage_cap_credits_override, p.overage_cap_credits),
        COALESCE(s.overage_price_per_credit_override, p.overage_price_per_credit),
        p.low_balance_threshold_credits,
        p.rollover_cap_credits,
        COALESCE(s.invoice_terms_days_override, p.invoice_terms_days),
        p.invoice_grace_hours,
        s.billing_contact_email
    FROM subscription.subscriptions s
    JOIN subscription.plans p ON p.id = s.plan_id
    WHERE s.id = p_subscription_id;
$function$;

-- 5. Expense budgets are USD budgets.
-- Same shape as the contract price: renamed in place, or folded into an amount_usd the EF model made.
DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM information_schema.columns
         WHERE table_schema = 'subscription' AND table_name = 'expense_budgets'
           AND column_name = 'amount_vnd'
    ) THEN
        IF EXISTS (
            SELECT 1 FROM information_schema.columns
             WHERE table_schema = 'subscription' AND table_name = 'expense_budgets'
               AND column_name = 'amount_usd'
        ) THEN
            UPDATE subscription.expense_budgets b
               SET amount_usd = round(b.amount_vnd / f.vnd_per_usd, 2)
              FROM usd_accounting_fx f;
            ALTER TABLE subscription.expense_budgets DROP COLUMN amount_vnd;
        ELSE
            ALTER TABLE subscription.expense_budgets RENAME COLUMN amount_vnd TO amount_usd;
            UPDATE subscription.expense_budgets b
               SET amount_usd = round(b.amount_usd / f.vnd_per_usd, 2)
              FROM usd_accounting_fx f;
        END IF;
    END IF;
END $$;

-- 6. Rate cards priced by the editor carried the accounting currency as part of their identity. The
-- unit price is credits either way; only the label moves. Settlement looks up CRD cards and is untouched.
UPDATE subscription.usage_rate_card
   SET currency = 'USD'
 WHERE currency = 'VND';
