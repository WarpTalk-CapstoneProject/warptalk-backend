-- Migration: 041-26-07-2026-phase3-contract-overage-settlement
-- Description:
--   Implements Phase 3 database primitives from credit-billing-master-plan.md:
--   contract term resolution, atomic usage settlement with overage/suspend state,
--   invoice overdue lookup support, and trial/domain guard columns.


ALTER TABLE subscription.subscriptions
    ADD COLUMN IF NOT EXISTS credits_per_cycle_override int,
    ADD COLUMN IF NOT EXISTS contract_price_vnd numeric(14, 2),
    ADD COLUMN IF NOT EXISTS overage_cap_credits_override int,
    ADD COLUMN IF NOT EXISTS overage_price_per_credit_override numeric(12, 4),
    -- Must be overridable alongside credits_per_cycle_override: settle_usage_charge compares
    -- the remaining balance directly against this threshold, so a contract that overrides the
    -- cycle allowance down (say 10k credits) while inheriting the plan-wide threshold (140k for
    -- Enterprise) would start below it and sit in 'low_balance' forever, making the warning noise.
    ADD COLUMN IF NOT EXISTS low_balance_threshold_credits_override int,
    ADD COLUMN IF NOT EXISTS invoice_terms_days_override int,
    ADD COLUMN IF NOT EXISTS billing_contact_email varchar(255),
    ADD COLUMN IF NOT EXISTS overage_credits_this_cycle int NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS overage_started_at timestamptz,
    ADD COLUMN IF NOT EXISTS service_state varchar(20) NOT NULL DEFAULT 'healthy',
    ADD COLUMN IF NOT EXISTS suspended_reason varchar(30),
    ADD COLUMN IF NOT EXISTS owner_email_domain varchar(255);

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'subscriptions_service_state_chk'
          AND conrelid = 'subscription.subscriptions'::regclass
    ) THEN
        ALTER TABLE subscription.subscriptions
            ADD CONSTRAINT subscriptions_service_state_chk
            CHECK (service_state IN ('healthy', 'low_balance', 'in_overage', 'suspended'));
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'subscriptions_suspended_reason_chk'
          AND conrelid = 'subscription.subscriptions'::regclass
    ) THEN
        ALTER TABLE subscription.subscriptions
            ADD CONSTRAINT subscriptions_suspended_reason_chk
            CHECK (
                suspended_reason IS NULL
                OR suspended_reason IN ('overage_cap', 'invoice_overdue', 'trial_ended')
            );
    END IF;
END $$;

CREATE UNIQUE INDEX IF NOT EXISTS ux_subscriptions_trial_owner_domain
ON subscription.subscriptions (lower(owner_email_domain))
WHERE deleted_at IS NULL
  AND trial_ends_at IS NOT NULL
  AND owner_email_domain IS NOT NULL;

CREATE INDEX IF NOT EXISTS ix_subscriptions_cycle_due
ON subscription.subscriptions (current_period_end)
WHERE deleted_at IS NULL
  AND is_active = true
  AND status = 'active';

CREATE INDEX IF NOT EXISTS ix_subscriptions_overdue_state
ON subscription.subscriptions (service_state, suspended_reason)
WHERE deleted_at IS NULL
  AND is_active = true;

CREATE OR REPLACE FUNCTION subscription.resolve_contract_terms(p_subscription_id uuid)
RETURNS TABLE (
    subscription_id uuid,
    plan_id uuid,
    credits_per_cycle int,
    contract_price_vnd numeric(14, 2),
    overage_cap_credits int,
    overage_price_per_credit numeric(12, 4),
    low_balance_threshold_credits int,
    rollover_cap_credits int,
    invoice_terms_days int,
    invoice_grace_hours int,
    billing_contact_email varchar(255)
)
LANGUAGE sql
STABLE
AS $$
    SELECT
        s.id,
        p.id,
        COALESCE(s.credits_per_cycle_override, p.credits_per_cycle),
        s.contract_price_vnd,
        COALESCE(s.overage_cap_credits_override, p.overage_cap_credits),
        COALESCE(s.overage_price_per_credit_override, p.overage_price_per_credit),
        COALESCE(s.low_balance_threshold_credits_override, p.low_balance_threshold_credits),
        p.rollover_cap_credits,
        COALESCE(s.invoice_terms_days_override, p.invoice_terms_days),
        p.invoice_grace_hours,
        s.billing_contact_email
    FROM subscription.subscriptions s
    JOIN subscription.plans p ON p.id = s.plan_id
    WHERE s.id = p_subscription_id;
$$;

CREATE OR REPLACE FUNCTION subscription.settle_usage_charge(
    p_subscription_id uuid,
    p_user_id uuid,
    p_workspace_id uuid,
    p_usage_type varchar,
    p_charge_type varchar,
    p_reference_id uuid,
    p_reference_type varchar,
    p_translation_room_id uuid,
    p_transcript_segment_id uuid,
    p_quantity numeric,
    p_unit varchar,
    p_credits_consumed int,
    p_idempotency_key varchar,
    p_pricing_rate_card_id uuid,
    p_unit_price_snapshot numeric,
    p_currency varchar,
    p_details jsonb DEFAULT '{}'::jsonb
)
RETURNS TABLE (
    applied boolean,
    transaction_id uuid,
    usage_record_id uuid,
    balance_after int,
    service_state varchar,
    suspended_reason varchar
)
LANGUAGE plpgsql
AS $$
DECLARE
    v_subscription subscription.subscriptions%ROWTYPE;
    v_terms record;
    v_existing subscription.credit_transactions%ROWTYPE;
    v_new_balance int;
    v_new_used int;
    v_new_overage int;
    v_overage_delta int;
    v_new_state varchar(20);
    v_new_reason varchar(30);
    v_usage_id uuid := uuidv7();
    v_tx_id uuid := uuidv7();
BEGIN
    IF p_credits_consumed <= 0 THEN
        RAISE EXCEPTION 'credits_consumed must be positive';
    END IF;

    -- The idempotency replay check deliberately lives *after* the FOR UPDATE below.
    -- Checking before the lock cannot be authoritative: two concurrent calls carrying the
    -- same key would both miss, both block on the lock, and only the post-lock check would
    -- stop the second one. An extra pre-lock probe would just add a race-y round trip that
    -- reads as if it were the guard.

    SELECT *
    INTO v_subscription
    FROM subscription.subscriptions
    WHERE id = p_subscription_id
      AND is_active = true
      AND deleted_at IS NULL
      AND status = 'active'
    FOR UPDATE;

    IF NOT FOUND THEN
        RETURN QUERY SELECT false, NULL::uuid, NULL::uuid, NULL::int, 'suspended'::varchar, 'trial_ended'::varchar;
        RETURN;
    END IF;

    IF v_subscription.service_state = 'suspended' THEN
        RETURN QUERY
        SELECT false, NULL::uuid, NULL::uuid, v_subscription.credits_remaining, v_subscription.service_state, v_subscription.suspended_reason;
        RETURN;
    END IF;

    IF p_idempotency_key IS NOT NULL THEN
        SELECT *
        INTO v_existing
        FROM subscription.credit_transactions
        WHERE idempotency_key = p_idempotency_key
        LIMIT 1;

        IF FOUND THEN
            RETURN QUERY
            SELECT
                false,
                v_existing.id,
                v_existing.usage_record_id,
                v_existing.balance_after,
                v_subscription.service_state,
                v_subscription.suspended_reason;
            RETURN;
        END IF;
    END IF;

    -- STRICT is defence-in-depth, not a fix for a reachable bug today: subscriptions.plan_id is
    -- NOT NULL and subscriptions_plan_id_fkey still guarantees the JOIN inside
    -- resolve_contract_terms matches, so it cannot currently return zero rows.
    -- It matters if that ever stops holding (this repo does drop cross-service FKs — see
    -- 043-30-07-2026-drop-cross-service-workspace-foreign-keys). A non-STRICT SELECT would leave
    -- every v_terms field NULL, overage_cap would COALESCE to 0, and the first overage credit
    -- would suspend the workspace with a misleading 'overage_cap' reason. Failing loudly beats
    -- silently suspending a paying customer.
    SELECT *
    INTO STRICT v_terms
    FROM subscription.resolve_contract_terms(p_subscription_id);

    v_new_balance := v_subscription.credits_remaining - p_credits_consumed;
    v_new_used := v_subscription.credits_used_this_cycle + p_credits_consumed;

    IF v_subscription.credits_remaining >= 0 THEN
        v_overage_delta := GREATEST(0, p_credits_consumed - v_subscription.credits_remaining);
    ELSE
        v_overage_delta := p_credits_consumed;
    END IF;

    v_new_overage := v_subscription.overage_credits_this_cycle + v_overage_delta;

    IF v_new_overage > COALESCE(v_terms.overage_cap_credits, 0) THEN
        UPDATE subscription.subscriptions
        SET service_state = 'suspended',
            suspended_reason = 'overage_cap',
            updated_at = now()
        WHERE id = p_subscription_id;

        RETURN QUERY
        SELECT false, NULL::uuid, NULL::uuid, v_subscription.credits_remaining, 'suspended'::varchar, 'overage_cap'::varchar;
        RETURN;
    END IF;

    IF v_new_overage = COALESCE(v_terms.overage_cap_credits, 0)
       AND COALESCE(v_terms.overage_cap_credits, 0) > 0 THEN
        v_new_state := 'suspended';
        v_new_reason := 'overage_cap';
    ELSIF v_new_balance < 0 THEN
        v_new_state := 'in_overage';
        v_new_reason := NULL;
    ELSIF v_new_balance <= COALESCE(v_terms.low_balance_threshold_credits, 0) THEN
        v_new_state := 'low_balance';
        v_new_reason := NULL;
    ELSE
        v_new_state := 'healthy';
        v_new_reason := NULL;
    END IF;

    INSERT INTO subscription.usage_records (
        id,
        subscription_id,
        user_id,
        workspace_id,
        translation_room_id,
        segment_id,
        usage_type,
        unit,
        quantity,
        credits_consumed,
        details,
        recorded_at
    )
    VALUES (
        v_usage_id,
        p_subscription_id,
        p_user_id,
        p_workspace_id,
        p_translation_room_id,
        p_transcript_segment_id,
        p_usage_type,
        p_unit,
        p_quantity,
        p_credits_consumed,
        COALESCE(p_details, '{}'::jsonb),
        now()
    );

    UPDATE subscription.subscriptions
    SET credits_remaining = v_new_balance,
        credits_used_this_cycle = v_new_used,
        overage_credits_this_cycle = v_new_overage,
        overage_started_at = CASE
            WHEN v_new_overage > 0 AND overage_started_at IS NULL THEN now()
            WHEN v_new_overage = 0 THEN NULL
            ELSE overage_started_at
        END,
        service_state = v_new_state,
        suspended_reason = v_new_reason,
        updated_at = now()
    WHERE id = p_subscription_id;

    INSERT INTO subscription.credit_transactions (
        id,
        subscription_id,
        user_id,
        workspace_id,
        amount,
        type,
        description,
        reference_id,
        reference_type,
        balance_after,
        charge_type,
        pricing_rate_card_id,
        usage_record_id,
        unit_price_snapshot,
        currency,
        idempotency_key,
        transcript_segment_id,
        created_at
    )
    VALUES (
        v_tx_id,
        p_subscription_id,
        COALESCE(p_user_id, v_subscription.user_id),
        p_workspace_id,
        -p_credits_consumed,
        'consume',
        CONCAT('Aggregated ', p_charge_type),
        p_reference_id,
        p_reference_type,
        v_new_balance,
        p_charge_type,
        p_pricing_rate_card_id,
        v_usage_id,
        p_unit_price_snapshot,
        COALESCE(p_currency, 'VND'),
        p_idempotency_key,
        p_transcript_segment_id,
        now()
    );

    RETURN QUERY
    SELECT true, v_tx_id, v_usage_id, v_new_balance, v_new_state, v_new_reason;
END;
$$;

