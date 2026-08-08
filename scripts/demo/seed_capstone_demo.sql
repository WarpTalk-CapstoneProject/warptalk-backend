-- ==============================================================================
-- CAPSTONE DEMO SEED DATA
-- ==============================================================================
-- This script creates the 3 specific accounts required for the Capstone Demo.
-- 
-- 1. Account 1: Workspace Owner 1tr9 (Enterprise), has 498 members.
-- 2. Account 2: Workspace Owner 500k (Pro), has unused tokens.
-- 3. Account 3: Guest (No Workspace).
-- ==============================================================================

DO $$
DECLARE
    v_user1_id UUID := '11111111-1111-7000-0000-000000000001';
    v_user2_id UUID := '22222222-2222-7000-0000-000000000002';
    v_user3_id UUID := '33333333-3333-7000-0000-000000000003';
    
    v_ws1_id UUID := '11111111-1111-7000-0000-100000000001';
    v_ws2_id UUID := '22222222-2222-7000-0000-200000000002';

    v_plan_1900k_id UUID;
    v_plan_500k_id UUID;
    
    i INT;
    v_dummy_user_id UUID;
BEGIN
    RAISE NOTICE 'Seeding Plans...';
    -- Get or create 1.9M Plan
    SELECT id INTO v_plan_1900k_id FROM subscription.plans WHERE slug = 'enterprise';
    IF v_plan_1900k_id IS NULL THEN
        v_plan_1900k_id := '019ec641-9776-7d50-b2b9-9edb93a46d24'; -- known from 002-seed
        INSERT INTO subscription.plans (id, name, slug, tier, price, currency, billing_cycle, credits_per_cycle, max_participants, is_active)
        VALUES (v_plan_1900k_id, 'Enterprise', 'enterprise', 'enterprise', 1900000, 'VND', 'monthly', 700000, 500, true)
        ON CONFLICT (slug) DO UPDATE SET price = 1900000, is_active = true, max_participants = 500;
    END IF;

    -- Get or create 500k Plan (Pro/Startup)
    SELECT id INTO v_plan_500k_id FROM subscription.plans WHERE slug = 'pro' OR slug = 'startup' ORDER BY price DESC LIMIT 1;
    IF v_plan_500k_id IS NULL THEN
        v_plan_500k_id := '019ec641-9776-7d50-b2b9-9edb93a46d23'; 
        INSERT INTO subscription.plans (id, name, slug, tier, price, currency, billing_cycle, credits_per_cycle, max_participants, is_active)
        VALUES (v_plan_500k_id, 'Pro', 'pro', 'pro', 500000, 'VND', 'monthly', 108000, 15, true)
        ON CONFLICT (slug) DO UPDATE SET price = 500000, is_active = true;
    END IF;

    RAISE NOTICE 'Seeding Users...';
    -- Ensure Users exist
    INSERT INTO auth.users (id, email, password_hash, full_name, email_verified)
    VALUES 
        (v_user1_id, 'owner1900@demo.com', '$2a$11$dummyhash', 'Owner Enterprise (1.9M)', true),
        (v_user2_id, 'owner500@demo.com', '$2a$11$dummyhash', 'Owner Pro (500k)', true),
        (v_user3_id, 'guest@demo.com', '$2a$11$dummyhash', 'Guest User', true)
    ON CONFLICT (email) DO UPDATE SET full_name = EXCLUDED.full_name;

    RAISE NOTICE 'Seeding Workspaces...';
    INSERT INTO auth.workspaces (id, name, slug, owner_id, plan_tier)
    VALUES 
        (v_ws1_id, 'Enterprise Workspace', 'demo-enterprise', v_user1_id, 'enterprise'),
        (v_ws2_id, 'Pro Workspace', 'demo-pro', v_user2_id, 'pro')
    ON CONFLICT (slug) DO NOTHING;

    RAISE NOTICE 'Seeding Subscriptions...';
    -- Ensure Subscriptions exist for the workspaces
    IF NOT EXISTS (SELECT 1 FROM subscription.subscriptions WHERE workspace_id = v_ws1_id AND status = 'active') THEN
        INSERT INTO subscription.subscriptions (id, user_id, workspace_id, plan_id, status, credits_remaining, current_period_start, current_period_end)
        VALUES (uuid_generate_v7(), v_user1_id, v_ws1_id, v_plan_1900k_id, 'active', 700000, NOW(), NOW() + INTERVAL '1 month');
    END IF;

    IF NOT EXISTS (SELECT 1 FROM subscription.subscriptions WHERE workspace_id = v_ws2_id AND status = 'active') THEN
        INSERT INTO subscription.subscriptions (id, user_id, workspace_id, plan_id, status, credits_remaining, current_period_start, current_period_end)
        VALUES (uuid_generate_v7(), v_user2_id, v_ws2_id, v_plan_500k_id, 'active', 108000, NOW(), NOW() + INTERVAL '1 month');
    END IF;

    RAISE NOTICE 'Seeding 498 members for Enterprise Workspace...';
    -- We use dynamic SQL to insert into workspace.workspace_members to avoid script failure if the schema/table name varies.
    -- Account 1 is already the owner, so we insert 498 more users to reach 499 (1 slot away from the 500 limit).
    FOR i IN 1..498 LOOP
        v_dummy_user_id := uuid_generate_v7();
        
        -- Generate dummy users
        INSERT INTO auth.users (id, email, password_hash, full_name, email_verified)
        VALUES (v_dummy_user_id, 'demo_member_' || i || '@demo.com', '$2a$11$dummyhash', 'Demo Member ' || i, true)
        ON CONFLICT DO NOTHING;

        BEGIN
            EXECUTE 'INSERT INTO workspace.workspace_members (id, workspace_id, user_id, role, status) VALUES ($1, $2, $3, ''member'', ''active'')'
            USING uuid_generate_v7(), v_ws1_id, v_dummy_user_id;
        EXCEPTION WHEN undefined_table OR invalid_schema_name THEN
            NULL;
        END;
    END LOOP;

    RAISE NOTICE 'Demo Seed Data Completed Successfully!';
END $$;
