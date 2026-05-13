-- ============================================================================
-- Migration: 20260506093000_billing_constraints_indexes_timestamp.sql
-- Description: Add business constraints, specialized indexes, and defaults.
-- ============================================================================

BEGIN;

-- ----------------------------------------------------------------------------
-- 1. ENFORCE COLUMN CONSTRAINTS
-- ----------------------------------------------------------------------------

-- Transactions: Amount and Status are mandatory
ALTER TABLE billing.transactions 
    ALTER COLUMN amount SET NOT NULL,
    ALTER COLUMN status SET NOT NULL;

-- ----------------------------------------------------------------------------
-- 2. CHECK CONSTRAINTS (Enum Validation)
-- ----------------------------------------------------------------------------

-- Subscriptions Status: Pending, Active, Cancelled, Expired, Suspended
DO $$ 
BEGIN 
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_subscriptions_status') THEN
        ALTER TABLE billing.subscriptions
            ADD CONSTRAINT chk_subscriptions_status
            CHECK (status IN ('Pending', 'Active', 'Cancelled', 'Expired', 'Suspended'));
    END IF;
END $$;

-- Transactions Status: Pending, Succeeded, Failed, Refunded, Cancelled
DO $$ 
BEGIN 
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_transactions_status') THEN
        ALTER TABLE billing.transactions
            ADD CONSTRAINT chk_transactions_status
            CHECK (status IN ('Pending', 'Succeeded', 'Failed', 'Refunded', 'Cancelled'));
    END IF;
END $$;

-- Credit Transactions Reference Type: Subscription, Transaction, Session, Manual, TopUp, Meeting, STT, TTS, Translation
DO $$ 
BEGIN 
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_credit_transactions_reference_type') THEN
        ALTER TABLE billing.credit_transactions
            ADD CONSTRAINT chk_credit_transactions_reference_type
            CHECK (
                reference_type IS NULL OR 
                reference_type IN ('Subscription', 'Transaction', 'Session', 'Manual', 'TopUp', 'Meeting', 'STT', 'TTS', 'Translation')
            );
    END IF;
END $$;

-- ----------------------------------------------------------------------------
-- 3. PERFORMANCE & INTEGRITY INDEXES
-- ----------------------------------------------------------------------------

-- Business Rule: One workspace can have at most one ACTIVE subscription
CREATE UNIQUE INDEX IF NOT EXISTS ux_subscriptions_workspace_active
    ON billing.subscriptions (workspace_id)
    WHERE (status = 'Active');

-- Ledger Optimization: Efficient retrieval of recent transactions for a workspace
CREATE INDEX IF NOT EXISTS idx_credit_transactions_workspace_created
    ON billing.credit_transactions (workspace_id, created_at DESC);

COMMIT;

-- ============================================================================
-- VALIDATION (Helper Queries)
-- ============================================================================
/*
-- Check Constraints
SELECT conname, pg_get_constraintdef(oid) FROM pg_constraint WHERE connamespace = 'billing'::regnamespace;

-- Check Indexes
SELECT indexname, indexdef FROM pg_indexes WHERE schemaname = 'billing';
*/
