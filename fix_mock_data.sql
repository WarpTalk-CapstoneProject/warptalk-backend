DO $$
DECLARE
    r RECORD;
    running_balance INT := 0;
    sub_id UUID;
BEGIN
    SELECT id INTO sub_id FROM subscription.subscriptions 
    WHERE workspace_id = '90d53dab-bb88-4f58-8198-5c0ccd643068' AND is_active = true LIMIT 1;
    
    UPDATE subscription.credit_transactions
    SET subscription_id = sub_id
    WHERE workspace_id = '90d53dab-bb88-4f58-8198-5c0ccd643068';
    
    FOR r IN 
        SELECT id, amount FROM subscription.credit_transactions 
        WHERE subscription_id = sub_id 
        ORDER BY created_at ASC
    LOOP
        running_balance := running_balance + r.amount;
        UPDATE subscription.credit_transactions 
        SET balance_after = running_balance 
        WHERE id = r.id;
    END LOOP;

    UPDATE subscription.subscriptions 
    SET credits_remaining = running_balance,
        credits_used_this_cycle = 160000 - running_balance
    WHERE id = sub_id;
END $$;
