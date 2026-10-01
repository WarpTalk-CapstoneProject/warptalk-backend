-- Migration: 20260925170000_add_billing_packages_manage_permission
-- Ticket: G11 — package management (/admin/packages)
-- Description:
--   Adds the staff permission that guards the new admin package endpoints in billing
--   (credit packs, add-ons, coupons and their Stripe sync): billing.packages_manage.
--
--   * One row in the permission catalog (WarpTalk.Shared.Authorization.AdminPermissions).
--   * Granted to the built-in Billing / Finance role, which already manages plans and pricing.
--     Super Admin needs no row: it holds every permission by rule. Custom roles are left alone —
--     an administrator decides whether they get it.
--
--   A new file rather than an edit of 20260925090000: an applied migration is immutable.
--   Idempotent (ON CONFLICT), no BEGIN/COMMIT — the runner owns the transaction.

INSERT INTO auth.permissions (code, description, group_name, is_active)
VALUES ('billing.packages_manage', 'Create, edit, archive and sync to Stripe the credit packs, add-ons and coupons.', 'billing', true)
ON CONFLICT (code) DO UPDATE
    SET description = EXCLUDED.description,
        group_name  = EXCLUDED.group_name,
        is_active   = true,
        deleted_at  = NULL,
        updated_at  = NOW();

INSERT INTO auth.role_permissions (role_id, permission_id)
SELECT r.id, p.id
FROM auth.roles r
JOIN auth.permissions p ON p.code = 'billing.packages_manage'
WHERE r.slug = 'billing_finance'
ON CONFLICT (role_id, permission_id) DO NOTHING;
