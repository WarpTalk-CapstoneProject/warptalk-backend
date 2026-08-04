-- 017-add-just-entered-overage-to-settlement.sql

-- Drop the old function because changing the return table structure requires it
DROP FUNCTION IF EXISTS subscription.settle_usage_charge(uuid, uuid, uuid, varchar, varchar, uuid, varchar, uuid, uuid, numeric, varchar, int, varchar, uuid, numeric, varchar, jsonb);

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
    suspended_reason varchar,
    just_entered_overage boolean
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

    SELECT id INTO v_existing
    FROM subscription.credit_transactions
    WHERE subscription_id = p_subscription_id
      AND reference_id = p_reference_id
      AND reference_type = p_reference_type
      AND charge_type = p_charge_type
      AND idempotency_key = p_idempotency_key
    LIMIT 1;

    IF FOUND THEN
        RETURN QUERY SELECT false, (NULL::uuid), (NULL::uuid), (NULL::int), (NULL::varchar), (NULL::varchar), false;
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
$$;
