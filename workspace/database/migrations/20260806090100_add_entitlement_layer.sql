-- Migration: 20260806090100_add_entitlement_layer
-- Ticket: WT-263 (schema), WT-294 (this file)
-- Description:
--   The workspace-schema half of scripts/migrations/050-05-08-2026-add-entitlement-layer.sql,
--   restated for the logical database WorkspaceService actually connects to.
--
--   WHY THIS FILE EXISTS. 050 created this table in the legacy monolith database `warptalk`.
--   WorkspaceService connects to `warptalk_workspace`, where the table does not exist. The deployed
--   WorkspaceDbContext maps it (WorkspaceDbContext.partial.cs), so the entitlement path is a 42703
--   waiting for its first read — the same defect that already took out three BillingService workers,
--   one database over. It has not fired yet only because nothing has driven the enforcement read on
--   this side in production.
--
--   The subscription-schema objects from 050 belong to BillingService's database and are restated in
--   billing/database/migrations/20260806090000_add_entitlement_layer.sql.
--
--   Idempotent and safe to re-run.

-- ─────────────────────────────────────────────────────────────
-- Replicated read model in WorkspaceService's own schema
-- ─────────────────────────────────────────────────────────────
-- This table is why meeting creation no longer depends on BillingService being reachable. Written
-- only by the billing.entitlements_changed consumer, read only by enforcement.
CREATE TABLE IF NOT EXISTS workspace.workspace_entitlement_snapshots (
    workspace_id            uuid        PRIMARY KEY,
    entitlements            jsonb       NOT NULL DEFAULT '{}'::jsonb,
    plan_slug               varchar(80),
    has_active_subscription boolean     NOT NULL DEFAULT false,
    resolved_at             timestamptz NOT NULL,
    last_event_id           uuid        NOT NULL,
    updated_at              timestamptz NOT NULL DEFAULT NOW()
);

COMMENT ON TABLE workspace.workspace_entitlement_snapshots IS
    'WT-263. Local replica of BillingService''s resolved entitlement map. A CACHE OF A DECISION, never an input to one - nothing here may be recomputed locally. resolved_at is the ordering guard: an event that resolved earlier than the stored row is ignored.';

COMMENT ON COLUMN workspace.workspace_entitlement_snapshots.entitlements IS
    'Resolved map as {key: {value, source}}. source is the provenance: platform_default | plan:<slug> | contract_override | workspace_override.';

GRANT SELECT, INSERT, UPDATE, DELETE
    ON workspace.workspace_entitlement_snapshots
    TO warptalk_workspace_runtime;
