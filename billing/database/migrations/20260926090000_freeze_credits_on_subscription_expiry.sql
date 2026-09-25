-- Migration: 20260926090000_freeze_credits_on_subscription_expiry
-- Ticket: expired-subscription gate — no billable AI without a live plan; frozen credits on expiry
-- Created At: 2026-09-26
-- Description:
--   THE LEAK. On 2026-09-24 a workspace whose only subscription had expired the day before
--   translated and dubbed a 14-minute meeting for free. billing_worker resolves a room's
--   subscription with `WHERE is_active`, found none, logged `no_subscription_for_room` and
--   returned — no charge, no refusal, so nothing ever stopped the room.
--
--   1. settle_usage_charge refuses a subscription that is not live.
--        * is_active = false (expired, superseded) or soft-deleted  -> refused, reason
--          'subscription_expired'. Returned only; the column's CHECK does not allow it and the
--          row's own state is not what changed. Stops a cached subscription id in billing_worker
--          from spending an ended plan's balance.
--        * suspended for 'trial_ended' / 'invoice_overdue' -> refused, reason kept. These used to
--          be APPLIED, and the balance arithmetic then overwrote the suspension with 'healthy'.
--          'overage_cap' is unchanged: the overage arithmetic already decides it, and a top-up
--          must still be able to clear it.
--      And a replay now returns its ORIGINAL transaction id. It returned NULLs, which both callers
--      read as a refusal — since WT-699 that stopped the room on every redelivered event.
--      Signature and result shape are unchanged, so CREATE OR REPLACE is enough (no DROP).
--
--   2. Frozen credits. When a subscription ends without renewal its remaining balance is split
--      (a subscription that ended BEFORE this migration ran is grandfathered: frozen whole):
--        * purchased (top-ups, unexpired credit packs) and admin-granted credits -> FROZEN
--        * plan-included credits up to the plan's rollover_cap_credits          -> FROZEN
--        * plan-included credits above the rollover cap                          -> FORFEITED
--      The balance moves to `frozen_credits` (not spendable: the row is inactive and (1) refuses
--      it), and comes back into the workspace's live subscription when it renews. Every move is a
--      credit_transactions row with an idempotency key. After the grace window (platform setting
--      billing.frozen_credits.grace_days, default 30) frozen credits are marked dormant — never
--      deleted.
--
--   Idempotent: IF NOT EXISTS / ON CONFLICT DO NOTHING / CREATE OR REPLACE. No BEGIN/COMMIT — the
--   migration runner owns the transaction.

ALTER TABLE subscription.subscriptions
    ADD COLUMN IF NOT EXISTS frozen_credits integer NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS credits_frozen_at timestamptz NULL,
    ADD COLUMN IF NOT EXISTS frozen_credits_dormant_at timestamptz NULL;

COMMENT ON COLUMN subscription.subscriptions.frozen_credits IS
    'Credits kept from a subscription that ended: purchased, admin-granted, and plan credits within the rollover cap. Not spendable; released into the workspace''s live subscription on renewal.';
COMMENT ON COLUMN subscription.subscriptions.credits_frozen_at IS
    'When the end-of-subscription credit split (freeze + forfeit) ran for this row. Set once, even when nothing was left to freeze; it is the idempotency marker of the split.';
COMMENT ON COLUMN subscription.subscriptions.frozen_credits_dormant_at IS
    'When frozen credits passed the grace window without a renewal. Dormant credits are still kept and still shown.';

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'subscriptions_frozen_credits_chk'
          AND conrelid = 'subscription.subscriptions'::regclass
    ) THEN
        ALTER TABLE subscription.subscriptions
            ADD CONSTRAINT subscriptions_frozen_credits_chk CHECK (frozen_credits >= 0);
    END IF;
END
$$;

CREATE INDEX IF NOT EXISTS ix_subscriptions_workspace_frozen
    ON subscription.subscriptions (workspace_id)
    WHERE frozen_credits > 0;

-- GRANDFATHERING (owner, 2026-09-25). The instant this migration runs is when the forfeit half of
-- the policy takes effect: a subscription that ended BEFORE it is frozen whole and forfeits
-- nothing. Recorded once (ON CONFLICT DO NOTHING keeps the first apply time on a re-run), in Unix
-- seconds. The platform setting billing.frozen_credits.policy_effective_at overrides it.
INSERT INTO subscription.billing_policy_config (key, value)
VALUES ('frozen_credit_policy_effective_epoch', extract(epoch FROM now()))
ON CONFLICT (key) DO NOTHING;

CREATE INDEX IF NOT EXISTS ix_subscriptions_ended_unsplit
    ON subscription.subscriptions (current_period_end)
    WHERE is_active = false AND credits_frozen_at IS NULL AND deleted_at IS NULL;

CREATE OR REPLACE FUNCTION subscription.settle_usage_charge(p_subscription_id uuid, p_user_id uuid, p_workspace_id uuid, p_usage_type character varying, p_charge_type character varying, p_reference_id uuid, p_reference_type character varying, p_translation_room_id uuid, p_transcript_segment_id uuid, p_quantity numeric, p_unit character varying, p_credits_consumed integer, p_idempotency_key character varying, p_pricing_rate_card_id uuid, p_unit_price_snapshot numeric, p_currency character varying, p_details jsonb DEFAULT '{}'::jsonb)
 RETURNS TABLE(applied boolean, transaction_id uuid, usage_record_id uuid, balance_after integer, service_state character varying, suspended_reason character varying, just_entered_overage boolean)
 LANGUAGE plpgsql
AS $function$
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
    v_just_entered_overage boolean := false;
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

    SELECT * INTO v_subscription
    FROM subscription.subscriptions
    WHERE id = p_subscription_id
    FOR UPDATE;

    IF NOT FOUND THEN
        RAISE EXCEPTION 'Subscription % not found', p_subscription_id;
    END IF;

    SELECT * INTO v_existing
    FROM subscription.credit_transactions
    WHERE subscription_id = p_subscription_id
      AND reference_id = p_reference_id
      AND reference_type = p_reference_type
      AND charge_type = p_charge_type
      AND idempotency_key = p_idempotency_key
    LIMIT 1;

    IF FOUND THEN
        -- A replay carries the ORIGINAL transaction. Both callers tell a replay from a refusal by
        -- exactly that (UsageService: TransactionId.HasValue; billing_worker: transaction_id IS
        -- NOT NULL), and until this returned NULLs every redelivered event read as a refusal —
        -- which, since WT-699, stops the room's translation.
        RETURN QUERY SELECT false, v_existing.id, v_existing.usage_record_id,
            v_subscription.credits_remaining, v_subscription.service_state,
            v_subscription.suspended_reason, false;
        RETURN;
    END IF;

    -- A subscription that is no longer the workspace's live plan pays for nothing. An expired or
    -- superseded row keeps its balance (frozen, see the header) but a charge must not spend it:
    -- the settlement used to apply against any row it was handed, so a cached subscription id kept
    -- billing a plan that had ended. Nothing is written — the row's own state is not the thing
    -- that changed.
    IF NOT v_subscription.is_active OR v_subscription.deleted_at IS NOT NULL THEN
        RETURN QUERY SELECT false, (NULL::uuid), (NULL::uuid), v_subscription.credits_remaining,
            'suspended'::varchar, 'subscription_expired'::varchar, false;
        RETURN;
    END IF;

    -- A trial that ended or an invoice left unpaid suspends the workspace. The balance checks
    -- below only know about overage, so a charge here used to be APPLIED and then overwrite the
    -- suspension with 'healthy' — the first translated sentence lifted it. Refuse, and keep the
    -- reason. An overage_cap suspension is still decided by the overage arithmetic below, which a
    -- top-up is meant to be able to clear.
    IF v_subscription.service_state = 'suspended'
       AND v_subscription.suspended_reason IN ('trial_ended', 'invoice_overdue') THEN
        RETURN QUERY SELECT false, (NULL::uuid), (NULL::uuid), v_subscription.credits_remaining,
            v_subscription.service_state, v_subscription.suspended_reason, false;
        RETURN;
    END IF;

    SELECT
        COALESCE(s.credits_per_cycle_override, p.credits_per_cycle) as credits_per_cycle,
        COALESCE(s.overage_cap_credits_override, p.overage_cap_credits) as overage_cap_credits,
        COALESCE(s.low_balance_threshold_credits_override, p.low_balance_threshold_credits) as low_balance_threshold_credits
    INTO v_terms
    FROM subscription.subscriptions s
    JOIN subscription.plans p ON p.id = s.plan_id
    WHERE s.id = p_subscription_id;

    v_new_balance := v_subscription.credits_remaining - p_credits_consumed;
    v_new_used := v_subscription.credits_used_this_cycle + p_credits_consumed;

    IF v_subscription.credits_remaining >= 0 THEN
        v_overage_delta := GREATEST(0, p_credits_consumed - v_subscription.credits_remaining);
    ELSE
        v_overage_delta := p_credits_consumed;
    END IF;

    v_new_overage := v_subscription.overage_credits_this_cycle + v_overage_delta;

    IF v_new_overage > 0 AND v_subscription.overage_started_at IS NULL THEN
        v_just_entered_overage := true;
    END IF;

    IF v_new_overage > v_terms.overage_cap_credits THEN
        UPDATE subscription.subscriptions
        SET service_state = 'suspended',
            suspended_reason = 'overage_cap',
            updated_at = now()
        WHERE id = p_subscription_id;

        RETURN QUERY SELECT false, (NULL::uuid), (NULL::uuid), v_subscription.credits_remaining, 'suspended'::varchar, 'overage_cap'::varchar, false;
        RETURN;
    END IF;

    IF v_new_overage = v_terms.overage_cap_credits
       AND v_terms.overage_cap_credits > 0 THEN
        v_new_state := 'suspended';
        v_new_reason := 'overage_cap';
    ELSIF v_new_balance < 0 THEN
        v_new_state := 'in_overage';
        v_new_reason := NULL;
    ELSIF v_new_balance <= v_terms.low_balance_threshold_credits THEN
        v_new_state := 'low_balance';
        v_new_reason := NULL;
    ELSE
        v_new_state := 'healthy';
        v_new_reason := NULL;
    END IF;

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
    ) VALUES (
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
    ) VALUES (
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

    RETURN QUERY SELECT true, v_tx_id, v_usage_id, v_new_balance, v_new_state, v_new_reason, v_just_entered_overage;
END;
$function$;
