-- Migration: 20260926120000_stripe_recurring_auto_renew
-- Ticket: backend #466 — auto_renew subscriptions can expire instead of renewing
-- Created At: 2026-09-26
-- Description:
--   Owner decision on #466: "auto-renew" MEANS RECURRING PAYMENT. A card customer who buys a plan
--   with auto-renew on gets a real Stripe Subscription; Stripe charges the saved card each cycle and
--   the invoice.paid webhook renews the row. The race in #466 (SubscriptionExpirationWorker and
--   BillingCycleWorker selecting the same rows, whichever ran first winning) is closed by giving
--   every row exactly ONE owner of its end-of-period transition:
--
--     renewal_mode = 'invoice'  BillingCycleWorker invoices and grants the next cycle (contracts).
--     renewal_mode = 'stripe'   Stripe charges; only the webhooks renew / enter dunning.
--     renewal_mode = 'none'     nothing renews it (one-off card purchase, trial); the sweep ends it.
--
--   1. subscriptions: renewal_mode, the Stripe subscription / customer it is linked to, Stripe's
--      last reported status, and the dunning state after a failed renewal charge
--      (payment_failed_at, payment_grace_ends_at — the grace window is the platform setting
--      billing.dunning.grace_days, default 7 — and the failure reason Stripe gave).
--   2. plans: the recurring Stripe Product / Prices an auto-renew checkout sells (created on the
--      first such checkout; Stripe objects are never deleted, a changed price archives the old one).
--   3. BACKFILL. Every existing row defaults to 'invoice' (what BillingCycleWorker assumed of every
--      auto_renew row). Rows that were bought by card — their FIRST payment is a Stripe one — and
--      trials become 'none', so the cycle close stops granting them a cycle nobody paid for. They
--      keep today's end-of-period behaviour until they renew; a renewal through Stripe links them
--      (renewal_mode = 'stripe'). Contract rows whose invoices were later paid by card keep
--      'invoice': their first payment is the internal invoice, not Stripe.
--
--   Idempotent: IF NOT EXISTS / guarded constraints / a backfill that only moves 'invoice' rows.
--   No BEGIN/COMMIT — the migration runner owns the transaction.

ALTER TABLE subscription.subscriptions
    ADD COLUMN IF NOT EXISTS renewal_mode varchar(16) NOT NULL DEFAULT 'invoice',
    ADD COLUMN IF NOT EXISTS stripe_subscription_id varchar(255) NULL,
    ADD COLUMN IF NOT EXISTS stripe_customer_id varchar(255) NULL,
    ADD COLUMN IF NOT EXISTS stripe_subscription_status varchar(32) NULL,
    ADD COLUMN IF NOT EXISTS payment_failed_at timestamptz NULL,
    ADD COLUMN IF NOT EXISTS payment_grace_ends_at timestamptz NULL,
    ADD COLUMN IF NOT EXISTS payment_failure_reason varchar(500) NULL;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'chk_subscriptions_renewal_mode'
          AND conrelid = 'subscription.subscriptions'::regclass
    ) THEN
        ALTER TABLE subscription.subscriptions
            ADD CONSTRAINT chk_subscriptions_renewal_mode CHECK (renewal_mode IN ('invoice', 'stripe', 'none'));
    END IF;
END
$$;

CREATE UNIQUE INDEX IF NOT EXISTS ux_subscriptions_stripe_subscription
    ON subscription.subscriptions (stripe_subscription_id)
    WHERE stripe_subscription_id IS NOT NULL;

COMMENT ON COLUMN subscription.subscriptions.renewal_mode IS
    '#466: who owns the end-of-period transition. invoice = BillingCycleWorker; stripe = the Stripe webhooks (invoice.paid / invoice.payment_failed); none = nothing renews it, the expiry sweep ends it.';
COMMENT ON COLUMN subscription.subscriptions.stripe_subscription_id IS
    '#466: the Stripe Subscription (sub_...) that charges this row each cycle.';
COMMENT ON COLUMN subscription.subscriptions.stripe_customer_id IS
    '#466: the Stripe Customer holding the saved card; the billing portal opens on it. Never card data.';
COMMENT ON COLUMN subscription.subscriptions.stripe_subscription_status IS
    '#466: Stripe''s status for stripe_subscription_id as last reported by a webhook.';
COMMENT ON COLUMN subscription.subscriptions.payment_failed_at IS
    '#466: first failed renewal charge of the current dunning episode; NULL when not in dunning.';
COMMENT ON COLUMN subscription.subscriptions.payment_grace_ends_at IS
    '#466: the plan stays in force until this instant after a failed renewal; then the sweep expires the row.';

ALTER TABLE subscription.plans
    ADD COLUMN IF NOT EXISTS stripe_product_id varchar(255) NULL,
    ADD COLUMN IF NOT EXISTS stripe_price_ids jsonb NOT NULL DEFAULT '{}'::jsonb;

COMMENT ON COLUMN subscription.plans.stripe_price_ids IS
    '#466: price key (monthly_vnd, yearly_usd, ...) -> recurring Stripe Price id used by auto-renew checkouts.';

-- Backfill (3): card-bought rows and trials are not the cycle close's to renew.
UPDATE subscription.subscriptions s
SET renewal_mode = 'none'
WHERE s.renewal_mode = 'invoice'
  AND s.stripe_subscription_id IS NULL
  AND (
        EXISTS (
            SELECT 1
            FROM subscription.payments p
            WHERE p.subscription_id = s.id
              AND p.provider = 'stripe'
              AND NOT EXISTS (
                  SELECT 1
                  FROM subscription.payments earlier
                  WHERE earlier.subscription_id = s.id
                    AND earlier.created_at < p.created_at
              )
        )
        OR (
            s.trial_ends_at IS NOT NULL
            AND NOT EXISTS (SELECT 1 FROM subscription.payments p WHERE p.subscription_id = s.id)
        )
      );
