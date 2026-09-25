-- Migration: 20260926090000_add_content_email_send_permission
-- Ticket: Email CMS v3 — custom email templates sent to an audience
-- Created At: 2026-09-26
-- Description:
--   One permission joins the staff catalog (WarpTalk.Shared.Authorization.AdminPermissions):
--
--     content.email_send   send a custom email template to an audience, or as an announcement's
--                          email channel
--
--   It is separate from content.email_templates on purpose: editing a template reaches nobody,
--   an audience send reaches every account in it. Built-in grants:
--     content_marketing  content.email_send
--     super_admin        nothing stored — every permission by rule.
--
--   Earlier staff-catalog migrations are applied and immutable, so the catalog grows here.
--   Idempotent (ON CONFLICT), no BEGIN/COMMIT — the runner owns the transaction. The auth test
--   StaffRbacDatabaseTests runs every staff-catalog migration in order and holds them equal to
--   AdminPermissions and BuiltInStaffRoles.

INSERT INTO auth.permissions (code, description, group_name, is_active)
VALUES
    ('content.email_send', 'Send a custom email template to an audience, or as an announcement''s email.', 'content', true)
ON CONFLICT (code) DO UPDATE
    SET description = EXCLUDED.description,
        group_name  = EXCLUDED.group_name,
        is_active   = true,
        deleted_at  = NULL,
        updated_at  = NOW();

INSERT INTO auth.role_permissions (role_id, permission_id)
SELECT r.id, p.id
FROM (VALUES
    ('content_marketing', 'content.email_send')
) AS grants (slug, code)
JOIN auth.roles r ON r.slug = grants.slug
JOIN auth.permissions p ON p.code = grants.code
ON CONFLICT (role_id, permission_id) DO NOTHING;
