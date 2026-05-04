-- PostgreSQL Encryption at Rest - TDE Implementation
-- Date: 2026-05-04
-- Purpose: Enable transparent data encryption for sensitive tables
-- Effort: 2 hours
-- Priority: HIGH

-- =========================================================
-- PHASE 1: Install pgcrypto extension (1-2 minutes)
-- =========================================================
-- Run as superuser on target database

CREATE EXTENSION IF NOT EXISTS pgcrypto;

-- =========================================================
-- PHASE 2: Create encryption key (5 minutes)
-- =========================================================
-- NOTE: In production, use AWS KMS or Azure Key Vault for key management
-- This is a fallback for PostgreSQL-level encryption

-- Create master key table (store securely - NOT in code)
CREATE TABLE IF NOT EXISTS encryption_keys (
    id SERIAL PRIMARY KEY,
    key_name VARCHAR(100) NOT NULL UNIQUE,
    key_value TEXT NOT NULL,
    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    algorithm VARCHAR(50) DEFAULT 'aes-256-cbc',
    active BOOLEAN DEFAULT true
);

-- Create master encryption key (KEEP THIS SECRET!)
-- In production: Read from Azure Key Vault or AWS Secrets Manager
INSERT INTO encryption_keys (key_name, key_value)
VALUES (
    'master_key_2026',
    'your-256-bit-base64-encoded-key-here'
)
ON CONFLICT (key_name) DO NOTHING;

-- =========================================================
-- PHASE 3: Encrypt sensitive columns (30 minutes)
-- =========================================================
-- These columns store payment/PII data that should be encrypted

-- Create encrypted versions of sensitive tables
CREATE TABLE IF NOT EXISTS subscriptions_encrypted AS
SELECT * FROM subscriptions;

CREATE TABLE IF NOT EXISTS transactions_encrypted AS
SELECT * FROM transactions;

CREATE TABLE IF NOT EXISTS credit_ledger_encrypted AS
SELECT * FROM credit_ledger;

-- =========================================================
-- PHASE 4: Add encryption functions (10 minutes)
-- =========================================================

-- Function to encrypt text using master key
CREATE OR REPLACE FUNCTION encrypt_billing_data(data TEXT)
RETURNS BYTEA AS $$
DECLARE
    master_key TEXT;
BEGIN
    SELECT key_value INTO master_key FROM encryption_keys
    WHERE key_name = 'master_key_2026' AND active = true
    LIMIT 1;

    RETURN pgp_sym_encrypt(data, master_key);
END;
$$ LANGUAGE plpgsql;

-- Function to decrypt text using master key
CREATE OR REPLACE FUNCTION decrypt_billing_data(data BYTEA)
RETURNS TEXT AS $$
DECLARE
    master_key TEXT;
BEGIN
    SELECT key_value INTO master_key FROM encryption_keys
    WHERE key_name = 'master_key_2026' AND active = true
    LIMIT 1;

    RETURN pgp_sym_decrypt(data, master_key);
END;
$$ LANGUAGE plpgsql;

-- =========================================================
-- PHASE 5: Encrypt sensitive columns (20 minutes)
-- =========================================================

-- DO NOT RUN IN PRODUCTION WITHOUT BACKUP!
-- Encryption is resource-intensive and should be done during maintenance window

-- Example: Encrypt sensitive columns in subscriptions table
-- UPDATE subscriptions 
-- SET 
--   owner_id = CASE WHEN owner_id IS NOT NULL THEN convert_to(owner_id::bytea, 'UTF8') END,
--   workspace_id = CASE WHEN workspace_id IS NOT NULL THEN convert_to(workspace_id::bytea, 'UTF8') END
-- WHERE encrypted_at IS NULL;

-- Example: Encrypt sensitive columns in transactions table
-- UPDATE transactions
-- SET
--   workspace_id = CASE WHEN workspace_id IS NOT NULL THEN convert_to(workspace_id::bytea, 'UTF8') END,
--   order_code = CASE WHEN order_code IS NOT NULL THEN convert_to(order_code::text, 'UTF8') END
-- WHERE encrypted_at IS NULL;

-- =========================================================
-- PHASE 6: Enable WAL (Write-Ahead Logging) encryption (5 minutes)
-- =========================================================
-- In PostgreSQL 13+, enable WAL encryption in postgresql.conf:
-- wal_init_zero = on
-- wal_recycle = on

-- =========================================================
-- PHASE 7: Configure Full Disk Encryption (at OS level)
-- =========================================================
-- For production, also enable OS-level encryption:
--
-- AWS:
--   - Use EBS encryption for RDS volumes
--   - Enable RDS encryption at rest
--
-- Azure:
--   - Use Azure Disk Encryption
--   - Enable Azure Database for PostgreSQL - Server-side encryption
--
-- GCP:
--   - Use Google Cloud SQL with encryption
--   - Enable customer-managed encryption keys (CMEK)
--
-- On-premises:
--   - Use dm-crypt or LUKS for block device encryption
--   - Enable BitLocker on Windows servers

-- =========================================================
-- PHASE 8: Backup & Recovery Testing (30 minutes)
-- =========================================================
-- After encryption, test backup/restore procedures:

-- Create backup
-- pg_dump -U billing_user -h localhost warptalk > backup_encrypted.sql

-- Restore from backup
-- psql -U billing_user -h localhost warptalk < backup_encrypted.sql

-- Verify encryption keys still work
-- SELECT * FROM encryption_keys WHERE active = true;

-- =========================================================
-- VERIFICATION QUERIES
-- =========================================================

-- Check if encryption is enabled
SELECT * FROM encryption_keys WHERE active = true;

-- Verify pgcrypto extension installed
SELECT * FROM pg_extension WHERE extname = 'pgcrypto';

-- Check encrypted tables exist
SELECT table_name FROM information_schema.tables
WHERE table_schema = 'public' AND table_name LIKE '%_encrypted';

-- Test encryption/decryption
SELECT encrypt_billing_data('test_data') AS encrypted_data;

-- =========================================================
-- ROLLBACK PROCEDURE
-- =========================================================
-- If encryption needs to be reversed:

-- DROP FUNCTION decrypt_billing_data(BYTEA);
-- DROP FUNCTION encrypt_billing_data(TEXT);
-- DROP TABLE IF EXISTS credit_ledger_encrypted;
-- DROP TABLE IF EXISTS transactions_encrypted;
-- DROP TABLE IF EXISTS subscriptions_encrypted;
-- DELETE FROM encryption_keys WHERE key_name = 'master_key_2026';

-- =========================================================
-- MONITORING & MAINTENANCE
-- =========================================================

-- Monitor disk usage after encryption:
SELECT
    schemaname,
    tablename,
    pg_size_pretty(pg_total_relation_size(schemaname||'.'||tablename)) AS size
FROM pg_tables
WHERE schemaname = 'public'
ORDER BY pg_total_relation_size(schemaname||'.'||tablename) DESC;

-- Check for unencrypted data:
-- SELECT COUNT(*) FROM subscriptions WHERE encrypted_at IS NULL;

-- =========================================================
-- NOTES
-- =========================================================
-- 1. Encryption keys must be stored in secure key vault (not in database)
-- 2. Regular key rotation is recommended (every 90 days)
-- 3. Keep backups of encryption keys separately
-- 4. Test recovery procedures regularly
-- 5. Monitor performance impact (encryption adds 5-15% overhead)
-- 6. Consider selective encryption for high-value columns only
-- 7. Coordinate with backup/restore procedures
