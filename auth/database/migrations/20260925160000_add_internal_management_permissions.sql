-- Migration: 20260925160000_add_internal_management_permissions
-- Ticket: G12 — internal management: operating expenses (/admin/finance/expenses) and the
--         pending-work inbox (/admin/inbox)
-- Created At: 2026-09-25
-- Description:
--   Four permissions join the staff catalog (WarpTalk.Shared.Authorization.AdminPermissions):
--
--     finance.read    view operating expenses, budgets, expense reports, the P&L with expenses
--     finance.manage  record, edit, import and delete expenses, receipts, categories, budgets
--     inbox.read               view the pending-work inbox (only items from areas the member can view)
--     inbox.manage             assign, snooze, annotate and close inbox items
--
--   Expenses get their own area rather than riding on billing.read: salaries are recorded there, and
--   the Support role reads billing. Built-in grants:
--     billing_finance    all four
--     support, content_marketing, operations_sre   inbox.read + inbox.manage (each sees only the
--                        sources its other permissions already allow)
--     read_only_auditor  finance.read + inbox.read (every read code, as the role is defined)
--     super_admin        nothing stored — every permission by rule.
--
--   20260925090000_add_platform_staff_rbac is applied and immutable, so the catalog grows here.
--   Idempotent (ON CONFLICT), no BEGIN/COMMIT — the runner owns the transaction. The auth test
--   StaffRbacDatabaseTests runs both files and holds them equal to AdminPermissions and BuiltInStaffRoles.

INSERT INTO auth.permissions (code, description, group_name, is_active)
VALUES
    ('finance.read', 'View operating expenses, budgets, expense reports and the profit and loss with expenses.', 'finance', true),
    ('finance.manage', 'Record, edit, import and delete operating expenses, receipts, categories and budgets.', 'finance', true),
    ('inbox.read', 'View the pending-work inbox (each item only from the areas the member can view).', 'inbox', true),
    ('inbox.manage', 'Assign, snooze, annotate and close items in the pending-work inbox.', 'inbox', true)
ON CONFLICT (code) DO UPDATE
    SET description = EXCLUDED.description,
        group_name  = EXCLUDED.group_name,
        is_active   = true,
        deleted_at  = NULL,
        updated_at  = NOW();

INSERT INTO auth.role_permissions (role_id, permission_id)
SELECT r.id, p.id
FROM (VALUES
    ('billing_finance', 'finance.read'),
    ('billing_finance', 'finance.manage'),
    ('billing_finance', 'inbox.read'),
    ('billing_finance', 'inbox.manage'),
    ('support', 'inbox.read'),
    ('support', 'inbox.manage'),
    ('content_marketing', 'inbox.read'),
    ('content_marketing', 'inbox.manage'),
    ('operations_sre', 'inbox.read'),
    ('operations_sre', 'inbox.manage'),
    ('read_only_auditor', 'finance.read'),
    ('read_only_auditor', 'inbox.read')
) AS grants (slug, code)
JOIN auth.roles r ON r.slug = grants.slug
JOIN auth.permissions p ON p.code = grants.code
ON CONFLICT (role_id, permission_id) DO NOTHING;
