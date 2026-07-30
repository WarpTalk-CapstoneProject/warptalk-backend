-- ============================================================================
-- Migration: 20260506094500_billing_idempotency_records.sql
-- Description: Table for persistent idempotency tracking (Prevent duplicate operations).
-- ============================================================================

BEGIN;

-- 1. Idempotency Records Table
CREATE TABLE IF NOT EXISTS billing.idempotency_records (
    id                  UUID PRIMARY KEY DEFAULT uuidv7(),
    idempotency_key     VARCHAR(255) NOT NULL,
    operation           VARCHAR(100) NOT NULL,
    workspace_id        UUID NULL,
    request_hash        VARCHAR(128) NOT NULL,
    response_json       TEXT NOT NULL,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    expires_at          TIMESTAMPTZ NOT NULL
);

-- 2. Prevent duplicate (Key + Operation) pairs
CREATE UNIQUE INDEX IF NOT EXISTS ux_idempotency_records_key_operation
    ON billing.idempotency_records (idempotency_key, operation);

-- 3. Optimize cleanup and lookup by workspace
CREATE INDEX IF NOT EXISTS idx_idempotency_records_workspace
    ON billing.idempotency_records (workspace_id);

COMMIT;
