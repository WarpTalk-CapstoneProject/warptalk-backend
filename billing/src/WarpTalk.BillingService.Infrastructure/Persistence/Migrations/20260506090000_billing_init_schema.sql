-- ============================================================================
-- Migration: 20260506090000_billing_init_schema.sql
-- Description: Initialize Billing service schema with core tables.
-- Tables: Plans, Subscriptions, Transactions, CreditTransactions.
-- ============================================================================

BEGIN;

-- 1. Create billing schema
CREATE SCHEMA IF NOT EXISTS billing;

-- 2. Plans table: Stores available subscription tiers (Free, Basic, Pro, etc.)
CREATE TABLE IF NOT EXISTS billing.plans (
    id                UUID PRIMARY KEY DEFAULT uuidv7(),
    name              VARCHAR(50) NOT NULL UNIQUE,
    price             DECIMAL(18, 2) NOT NULL,
    credits_per_month INTEGER NOT NULL,
    is_active         BOOLEAN NOT NULL DEFAULT TRUE,
    created_at        TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at        TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- 3. Subscriptions table: Tracks workspace subscription status and current credits
CREATE TABLE IF NOT EXISTS billing.subscriptions (
    id                UUID PRIMARY KEY DEFAULT uuidv7(),
    workspace_id      UUID NOT NULL,
    plan_id           UUID NOT NULL REFERENCES billing.plans(id) ON DELETE RESTRICT,
    status            VARCHAR(20) NOT NULL DEFAULT 'Pending',
    current_credits   INTEGER NOT NULL DEFAULT 0,
    start_date        TIMESTAMPTZ NOT NULL,
    end_date          TIMESTAMPTZ,
    row_version       BYTEA,
    created_at        TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at        TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- 4. Transactions table: Payment records for auditing and history
CREATE TABLE IF NOT EXISTS billing.transactions (
    id                UUID PRIMARY KEY DEFAULT uuidv7(),
    workspace_id      UUID NOT NULL,
    subscription_id   UUID REFERENCES billing.subscriptions(id) ON DELETE SET NULL,
    amount            DECIMAL(18, 2),
    status            VARCHAR(20),
    external_id       VARCHAR(255) UNIQUE,
    created_at        TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- 5. Credit Transactions table: Ledger tracking all credit changes (TopUp/Consume)
CREATE TABLE IF NOT EXISTS billing.credit_transactions (
    id                UUID PRIMARY KEY DEFAULT uuidv7(),
    workspace_id      UUID NOT NULL,
    amount            INTEGER NOT NULL,
    type              VARCHAR(20) NOT NULL,
    reference_id      UUID,
    reference_type    VARCHAR(50),
    created_at        TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

COMMIT;

-- ============================================================================
-- DOWN MIGRATION (Manual Rollback)
-- ============================================================================
/*
BEGIN;
    DROP TABLE IF EXISTS billing.credit_transactions CASCADE;
    DROP TABLE IF EXISTS billing.transactions CASCADE;
    DROP TABLE IF EXISTS billing.subscriptions CASCADE;
    DROP TABLE IF EXISTS billing.plans CASCADE;
    DROP SCHEMA IF EXISTS billing CASCADE;
COMMIT;
*/
