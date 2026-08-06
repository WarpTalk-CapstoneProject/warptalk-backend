-- Migration: 20260806090000_add_entitlement_layer
-- Ticket: WT-263 (schema), WT-294 (this file)
-- Description:
--   The subscription-schema half of scripts/migrations/050-05-08-2026-add-entitlement-layer.sql,
--   restated for the logical database BillingService actually connects to.
--
--   WHY THIS FILE EXISTS. 050 was written only into warptalk-infrastructure/scripts/migrations/,
--   which run-migrations.sh applies to the legacy monolith database `warptalk`. BillingService has
--   connected to `warptalk_billing` since the logical-database extraction, so 050 reported success
--   against a database no service reads. The deployed BillingDbContext maps all three objects below
--   (BillingDbContext.Custom.cs), so every SELECT that touched them raised 42703 undefined_column:
--   on 2026-08-06 that took out SubscriptionExpirationWorker, InvoiceOverdueSweeper and
--   BillingCycleWorker, which are periodic and therefore keep failing on each tick.
--
--   This file is the same DDL, minus the workspace-schema table — that one belongs to
--   WorkspaceService's database and is restated in
--   workspace/database/migrations/20260806090100_add_entitlement_layer.sql.
--
--   Idempotent and safe to re-run. It is also safe to apply to a database that already received
--   050 by some other route: every statement is IF NOT EXISTS or ON CONFLICT-equivalent.

-- ─────────────────────────────────────────────────────────────
-- 1. Plan layer: max_active_rooms
-- ─────────────────────────────────────────────────────────────
-- A real column, not a key in the plans.features JSON blob. Every other hard quota the plan sells
-- (max_participants, max_languages, credits_per_cycle) is a typed column; `features` is an
-- unvalidated marketing bag. Default 5 matches WorkspaceConstants.DefaultWorkspaceMaxActiveRooms
-- and EntitlementConstants.PlatformDefaults.MaxActiveRooms, which is the value the deployed
-- BillingDbContext declares as this column's default.
ALTER TABLE subscription.plans
    ADD COLUMN IF NOT EXISTS max_active_rooms integer NOT NULL DEFAULT 5;

COMMENT ON COLUMN subscription.plans.max_active_rooms IS
    'WT-263. Plan-level ceiling on concurrently active rooms. Resolved as the max_active_rooms entitlement; a workspace may tighten below it but never above.';

-- Keep the seeded Enterprise plan meaningfully above the platform floor rather than silently
-- inheriting it. 50 is the validated ceiling WorkspaceSettingsValidator already enforces (1..50).
-- Guarded on the column having just been defaulted, so a re-run cannot stamp over a value an
-- operator has since chosen through plan CRUD.
UPDATE subscription.plans
SET max_active_rooms = 50
WHERE slug = 'enterprise'
  AND deleted_at IS NULL
  AND max_active_rooms = 5;

-- ─────────────────────────────────────────────────────────────
-- 2. Contract layer: negotiated per-subscription entitlement terms
-- ─────────────────────────────────────────────────────────────
-- jsonb rather than a column per key: the entitlement key set grows with each new capability, and a
-- column per key would force a migration every time one is added. The typed *_override columns
-- beside it stay as they are — they carry commercial terms (credits, overage cap, invoice days),
-- none of which is a capability.
ALTER TABLE subscription.subscriptions
    ADD COLUMN IF NOT EXISTS entitlement_overrides jsonb;

COMMENT ON COLUMN subscription.subscriptions.entitlement_overrides IS
    'WT-263. Contract-negotiated entitlement overrides, keyed by entitlement key, e.g. {"max_languages": 5}. Outranks the plan in BOTH directions - a contract may grant more than the catalog row.';

-- ─────────────────────────────────────────────────────────────
-- 3. Workspace self-service layer
-- ─────────────────────────────────────────────────────────────
-- Lives in the BILLING schema even though a workspace owner sets it, because the resolver is the
-- only code permitted to compute an entitlement and it cannot enforce "tighten but never loosen"
-- against a value it cannot see. One row per (workspace, key) rather than a JSON bag so clearing one
-- setting cannot race a concurrent change to another.
CREATE TABLE IF NOT EXISTS subscription.workspace_entitlement_overrides (
    workspace_id    uuid        NOT NULL,
    entitlement_key varchar(60) NOT NULL,
    value           varchar(40) NOT NULL,
    set_by          uuid,
    updated_at      timestamptz NOT NULL DEFAULT NOW(),
    CONSTRAINT workspace_entitlement_overrides_pkey PRIMARY KEY (workspace_id, entitlement_key)
);

COMMENT ON TABLE subscription.workspace_entitlement_overrides IS
    'WT-263. A workspace''s own tightening of an entitlement. Values are stored as text so one table serves both numeric limits and boolean capabilities; BillingService parses each against the key''s declared shape. No cross-service FK to the workspace table, per the FK policy in migration 043.';

GRANT SELECT, INSERT, UPDATE, DELETE
    ON subscription.workspace_entitlement_overrides
    TO warptalk_billing_runtime;
