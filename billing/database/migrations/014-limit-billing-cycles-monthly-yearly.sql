ALTER TABLE subscription.plans
    DROP CONSTRAINT IF EXISTS chk_billing_cycle;

ALTER TABLE subscription.plans
    ADD CONSTRAINT chk_billing_cycle
    CHECK (billing_cycle IN ('monthly', 'yearly'));
