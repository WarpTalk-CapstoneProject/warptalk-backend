-- Migration: 20260925210000_add_settings_security_permission
-- Ticket: Platform settings console (/admin/settings)
-- Created At: 2026-09-25
-- Description:
--   /admin/settings grows from two panels into a settings console over a typed registry
--   (WarpTalk.Shared.PlatformSettings). One permission joins the staff catalog
--   (WarpTalk.Shared.Authorization.AdminPermissions):
--
--     settings.security   edit the Security & auth category (session lifetimes, password policy,
--                         login rate limits, lockout) — on top of settings.manage
--
--   No built-in role is granted it: a setting that can lock every user out, or lengthen every
--   session, is Super Admin's unless a Super Admin hands it to a custom role. super_admin holds it by
--   rule, with nothing stored.
--
--   settings.read and settings.manage keep their codes; their descriptions now cover the console.
--
--   20260925090000_add_platform_staff_rbac is applied and immutable, so the catalog grows here.
--   Idempotent (ON CONFLICT), no BEGIN/COMMIT — the runner owns the transaction. The auth test
--   StaffRbacDatabaseTests runs every staff-catalog migration in order and holds them equal to
--   AdminPermissions and BuiltInStaffRoles.

INSERT INTO auth.permissions (code, description, group_name, is_active)
VALUES
    ('settings.read', 'View platform settings, their history and integration status, the language catalog and the billing policy.', 'settings', true),
    ('settings.manage', 'Change, reset, revert, import and export platform settings (except Security), the language catalog and the billing policy.', 'settings', true),
    ('settings.security', 'Change the Security & auth platform settings: session lifetimes, password policy, login rate limits and lockout.', 'settings', true)
ON CONFLICT (code) DO UPDATE
    SET description = EXCLUDED.description,
        group_name  = EXCLUDED.group_name,
        is_active   = true,
        deleted_at  = NULL,
        updated_at  = NOW();
