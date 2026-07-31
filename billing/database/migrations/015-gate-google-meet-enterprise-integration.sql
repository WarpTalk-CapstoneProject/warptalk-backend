-- Migration: 015-gate-google-meet-enterprise-integration
-- Description:
--   External meeting integration scope for the capstone demo is Google Meet only.
--   The Enterprise plan advertises that capability explicitly.
--
-- Scope note — the matching CHECK constraints on integration.external_platform_accounts
-- and integration.external_meeting_sessions are deliberately NOT applied here. The
-- `integration` schema does not exist in warptalk_billing; it lives only in the shared
-- warptalk database (there is no warptalk_integration logical database), so guarding
-- those tables belongs to the migration path that owns that schema —
-- scripts/migrations/048-30-07-2026-gate-google-meet-enterprise-integration.sql in
-- warptalk-infrastructure, which already does exactly that against the shared database.
--
-- Attempting it from here does not merely no-op, it aborts the migration:
-- 'integration.external_platform_accounts'::regclass raises "relation does not exist"
-- even though it sits behind a `to_regclass(...) IS NOT NULL` guard in the same
-- condition, because the cast is still evaluated. Confirmed by replaying this file
-- against a schema-only copy of the production warptalk_billing database.

UPDATE subscription.plans
SET
    features = jsonb_set(
        jsonb_set(
            COALESCE(features, '{}'::jsonb),
            '{external_integrations}',
            '{"google_meet": true}'::jsonb,
            true
        ),
        '{supported_external_platforms}',
        '["google_meet"]'::jsonb,
        true
    ),
    updated_at = now()
WHERE slug = 'enterprise';
