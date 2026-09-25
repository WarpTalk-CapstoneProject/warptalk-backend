-- Migration: 20260925090000_add_platform_staff_rbac
-- Ticket: G10 — platform staff and role management for the admin portal
-- Created At: 2026-09-25
-- Description:
--   Until now the admin portal had one gate: the platform role 'admin' in auth.user_roles, read
--   from the access token. Everyone with it could do everything, and taking it away took effect
--   only when their token expired. This adds real staff roles with fine-grained permissions:
--
--   * auth.roles gains scope ('legacy' | 'platform_staff') and slug. The six legacy rows
--     (admin, user, moderator, Owner, Admin, Member) stay exactly as they are, scope 'legacy'.
--   * auth.permissions / auth.role_permissions (created in init-db.sql, never used) now hold the
--     permission catalog — WarpTalk.Shared.Authorization.AdminPermissions, one code per kind of
--     admin endpoint — and what each staff role grants.
--   * auth.staff_members: one row per staff person — role, active/suspended, last activity.
--   * auth.staff_invitations: staff access offered to an address with no account yet; accepted on
--     that address's first VERIFIED sign-in.
--
--   LOCK-OUT: every account holding an unrevoked legacy 'admin' role becomes an active Super Admin
--   here. The legacy rows are NOT removed, so rolling the services back to pre-G10 code keeps
--   every admin working. (New code ignores them; removing or suspending a staff member deletes
--   theirs, so a rollback cannot resurrect access G10 took away.) The auth service also enrols
--   a legacy holder it finds with no staff row, so a seed run after this migration cannot lock
--   anybody out either.
--
--   Super Admin has no role_permissions rows on purpose: it holds every permission by rule, so a
--   permission added later is Super Admin's without a migration.
--
--   Idempotent throughout (IF NOT EXISTS / ON CONFLICT), no BEGIN/COMMIT — the runner owns the
--   transaction. Seed values must match AdminPermissions.cs and BuiltInStaffRoles; the auth test
--   StaffRbacDatabaseTests.Migration_SeedsExactlyTheCatalogAndTheBuiltInRoles runs this file
--   against PostgreSQL and holds them together.

-- ── Roles: scope and slug ─────────────────────────────────────────────────────────────────────
ALTER TABLE auth.roles
    ADD COLUMN IF NOT EXISTS scope VARCHAR(20) NOT NULL DEFAULT 'legacy';

ALTER TABLE auth.roles
    ADD COLUMN IF NOT EXISTS slug VARCHAR(60);

DO $roles_scope$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'roles_scope_check' AND conrelid = 'auth.roles'::regclass
    ) THEN
        ALTER TABLE auth.roles
            ADD CONSTRAINT roles_scope_check CHECK (scope IN ('legacy', 'platform_staff'));
    END IF;
END
$roles_scope$;

CREATE UNIQUE INDEX IF NOT EXISTS roles_slug_key
    ON auth.roles (slug)
    WHERE slug IS NOT NULL;

COMMENT ON COLUMN auth.roles.scope IS
    'legacy = the pre-G10 platform/workspace roles (admin, user, moderator, Owner, Admin, Member); '
    'platform_staff = an admin-portal staff role (built-in when is_system, else custom).';
COMMENT ON COLUMN auth.roles.slug IS
    'Stable key of a staff role (super_admin, support, custom_...). NULL for legacy and deleted roles.';

-- ── Staff members ─────────────────────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS auth.staff_members (
    id                UUID PRIMARY KEY DEFAULT (uuidv7()),
    user_id           UUID NOT NULL,
    role_id           UUID NOT NULL,
    status            VARCHAR(20) NOT NULL DEFAULT 'active',
    status_reason     VARCHAR(500),
    status_changed_at TIMESTAMPTZ,
    status_changed_by UUID,
    source            VARCHAR(30) NOT NULL,
    invited_by        UUID,
    last_active_at    TIMESTAMPTZ,
    created_at        TIMESTAMPTZ NOT NULL DEFAULT (NOW()),
    updated_at        TIMESTAMPTZ NOT NULL DEFAULT (NOW()),
    updated_by        UUID,
    CONSTRAINT staff_members_user_id_key UNIQUE (user_id),
    CONSTRAINT staff_members_user_id_fkey FOREIGN KEY (user_id) REFERENCES auth.users (id),
    CONSTRAINT staff_members_role_id_fkey FOREIGN KEY (role_id) REFERENCES auth.roles (id),
    CONSTRAINT staff_members_status_check CHECK (status IN ('active', 'suspended')),
    CONSTRAINT staff_members_source_check
        CHECK (source IN ('migrated', 'invited', 'invitation_accepted', 'legacy_bridge'))
);

CREATE INDEX IF NOT EXISTS staff_members_role_id_idx ON auth.staff_members (role_id);

COMMENT ON TABLE auth.staff_members IS
    'G10: people who work on the WarpTalk platform itself, and the staff role each holds. '
    'Removing staff access deletes the row; the platform audit log keeps the history.';
COMMENT ON COLUMN auth.staff_members.last_active_at IS
    'Last admin request that reached the auth service''s access check. Written at most every 5 minutes.';

-- ── Staff invitations ─────────────────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS auth.staff_invitations (
    id               UUID PRIMARY KEY DEFAULT (uuidv7()),
    email            VARCHAR(255) NOT NULL,
    role_id          UUID NOT NULL,
    invited_by       UUID NOT NULL,
    note             VARCHAR(500),
    created_at       TIMESTAMPTZ NOT NULL DEFAULT (NOW()),
    expires_at       TIMESTAMPTZ NOT NULL,
    accepted_at      TIMESTAMPTZ,
    accepted_user_id UUID,
    revoked_at       TIMESTAMPTZ,
    revoked_by       UUID,
    revoke_reason    VARCHAR(500),
    CONSTRAINT staff_invitations_role_id_fkey FOREIGN KEY (role_id) REFERENCES auth.roles (id),
    CONSTRAINT staff_invitations_email_lower_check CHECK (email = lower(email))
);

-- At most one open invitation per address. Expiry is left to the reader (it is a time, not a
-- state), so an expired-but-unrevoked row still blocks a duplicate until someone revokes it or a
-- new one replaces it — the service revokes the stale one first.
CREATE UNIQUE INDEX IF NOT EXISTS staff_invitations_open_email_key
    ON auth.staff_invitations (email)
    WHERE accepted_at IS NULL AND revoked_at IS NULL;

COMMENT ON TABLE auth.staff_invitations IS
    'G10: staff access offered to an address with no account yet. Bound to the address, not to a '
    'link: accepted the first time an account with that VERIFIED email signs in.';

-- ── Permission catalog ────────────────────────────────────────────────────────────────────────
INSERT INTO auth.permissions (code, description, group_name, is_active)
VALUES
    ('workspaces.read', 'View workspaces, their members and the admin timeline.', 'workspaces', true),
    ('workspaces.write', 'Add internal notes, send notices and export a workspace''s data.', 'workspaces', true),
    ('workspaces.lifecycle', 'Suspend, reactivate, delete a workspace or transfer its ownership.', 'workspaces', true),
    ('accounts.read', 'View platform accounts and the voice-consent summary.', 'accounts', true),
    ('accounts.manage', 'Sign accounts out, deactivate, reactivate and unlock them.', 'accounts', true),
    ('meetings.read', 'View the meetings directory, meeting insights and meeting feedback.', 'meetings', true),
    ('billing.read', 'View revenue insights, subscriptions, invoices, usage, rate cards, plans and sales leads.', 'billing', true),
    ('billing.adjust_credit', 'Grant or deduct a workspace''s credits.', 'billing', true),
    ('billing.payments_manage', 'Record a manual payment and mark an invoice paid.', 'billing', true),
    ('billing.subscriptions_manage', 'Change plans, extend trials, comp periods, set entitlements and contract terms.', 'billing', true),
    ('billing.plans_manage', 'Create and edit sellable plans.', 'billing', true),
    ('billing.pricing_manage', 'Edit rate cards, provider costs, the pricing configuration and the FX rate.', 'billing', true),
    ('billing.leads_manage', 'Move enterprise sales leads through their statuses.', 'billing', true),
    ('plugins.read', 'View the plugin marketplace catalog and per-workspace availability.', 'plugins', true),
    ('plugins.manage', 'Add, edit, retire and delete plugins and set per-workspace availability.', 'plugins', true),
    ('content.announcements', 'Write, publish and archive platform announcements.', 'content', true),
    ('content.email_templates', 'Edit, restore and test-send transactional email templates.', 'content', true),
    ('glossary.read', 'View the platform glossary and its history.', 'glossary', true),
    ('glossary.manage', 'Create, edit, publish, archive and import platform glossary terms.', 'glossary', true),
    ('settings.read', 'View the language catalog and the billing policy.', 'settings', true),
    ('settings.manage', 'Edit the language catalog and the billing policy (VAT).', 'settings', true),
    ('health.read', 'View system health, Grafana dashboards and dead-lettered events.', 'operations', true),
    ('health.operate', 'Replay dead-lettered events.', 'operations', true),
    ('providers.read', 'View AI provider cost, latency and uptime.', 'operations', true),
    ('audit.read', 'Read the platform audit log.', 'audit', true),
    ('audit.export', 'Export the platform audit log.', 'audit', true),
    ('staff.read', 'View staff members, roles and who holds each permission.', 'staff', true),
    ('staff.manage', 'Invite staff, change their role, suspend or remove them, and edit roles.', 'staff', true),
    ('warpbot.use', 'Use the platform WarpBot assistant.', 'assistant', true)
ON CONFLICT (code) DO UPDATE
    SET description = EXCLUDED.description,
        group_name  = EXCLUDED.group_name,
        is_active   = true,
        deleted_at  = NULL,
        updated_at  = NOW();

-- ── Built-in staff roles ──────────────────────────────────────────────────────────────────────
INSERT INTO auth.roles (name, slug, description, is_system, is_active, scope)
VALUES
    ('Super Admin', 'super_admin', 'Every permission, including managing staff. Cannot be edited.', true, true, 'platform_staff'),
    ('Billing / Finance', 'billing_finance', 'Revenue, subscriptions, credits, invoices, plans and pricing.', true, true, 'platform_staff'),
    ('Support', 'support', 'Helps customers: workspaces, accounts and meetings, with read access to billing.', true, true, 'platform_staff'),
    ('Content / Marketing', 'content_marketing', 'Announcements, email templates and the platform glossary.', true, true, 'platform_staff'),
    ('Operations / SRE', 'operations_sre', 'System health, providers, the plugin catalog and platform settings.', true, true, 'platform_staff'),
    ('Read-only Auditor', 'read_only_auditor', 'Sees everything, changes nothing. Can export the audit log.', true, true, 'platform_staff')
ON CONFLICT (slug) WHERE slug IS NOT NULL DO UPDATE
    SET name        = EXCLUDED.name,
        description = EXCLUDED.description,
        is_system   = true,
        is_active   = true,
        scope       = 'platform_staff',
        updated_at  = NOW();

-- What each built-in role grants (Super Admin: nothing stored — every permission by rule).
INSERT INTO auth.role_permissions (role_id, permission_id)
SELECT r.id, p.id
FROM (VALUES
    ('billing_finance', 'billing.read'),
    ('billing_finance', 'billing.adjust_credit'),
    ('billing_finance', 'billing.payments_manage'),
    ('billing_finance', 'billing.subscriptions_manage'),
    ('billing_finance', 'billing.plans_manage'),
    ('billing_finance', 'billing.pricing_manage'),
    ('billing_finance', 'billing.leads_manage'),
    ('billing_finance', 'workspaces.read'),
    ('billing_finance', 'accounts.read'),
    ('billing_finance', 'providers.read'),
    ('billing_finance', 'settings.read'),
    ('billing_finance', 'audit.read'),
    ('billing_finance', 'warpbot.use'),
    ('support', 'workspaces.read'),
    ('support', 'workspaces.write'),
    ('support', 'accounts.read'),
    ('support', 'accounts.manage'),
    ('support', 'meetings.read'),
    ('support', 'billing.read'),
    ('support', 'plugins.read'),
    ('support', 'glossary.read'),
    ('support', 'settings.read'),
    ('support', 'audit.read'),
    ('support', 'warpbot.use'),
    ('content_marketing', 'content.announcements'),
    ('content_marketing', 'content.email_templates'),
    ('content_marketing', 'glossary.read'),
    ('content_marketing', 'glossary.manage'),
    ('content_marketing', 'workspaces.read'),
    ('content_marketing', 'meetings.read'),
    ('content_marketing', 'settings.read'),
    ('content_marketing', 'warpbot.use'),
    ('operations_sre', 'health.read'),
    ('operations_sre', 'health.operate'),
    ('operations_sre', 'providers.read'),
    ('operations_sre', 'plugins.read'),
    ('operations_sre', 'plugins.manage'),
    ('operations_sre', 'settings.read'),
    ('operations_sre', 'settings.manage'),
    ('operations_sre', 'workspaces.read'),
    ('operations_sre', 'accounts.read'),
    ('operations_sre', 'meetings.read'),
    ('operations_sre', 'audit.read'),
    ('operations_sre', 'warpbot.use'),
    ('read_only_auditor', 'workspaces.read'),
    ('read_only_auditor', 'accounts.read'),
    ('read_only_auditor', 'meetings.read'),
    ('read_only_auditor', 'billing.read'),
    ('read_only_auditor', 'plugins.read'),
    ('read_only_auditor', 'glossary.read'),
    ('read_only_auditor', 'settings.read'),
    ('read_only_auditor', 'health.read'),
    ('read_only_auditor', 'providers.read'),
    ('read_only_auditor', 'audit.read'),
    ('read_only_auditor', 'staff.read'),
    ('read_only_auditor', 'audit.export')
) AS grants (slug, code)
JOIN auth.roles r ON r.slug = grants.slug
JOIN auth.permissions p ON p.code = grants.code
ON CONFLICT (role_id, permission_id) DO NOTHING;

-- ── Existing system administrators become Super Admins ────────────────────────────────────────
INSERT INTO auth.staff_members (user_id, role_id, status, source)
SELECT DISTINCT ur.user_id, super_admin.id, 'active', 'migrated'
FROM auth.user_roles ur
JOIN auth.roles legacy_admin
    ON legacy_admin.id = ur.role_id
   AND legacy_admin.name = 'admin'
   AND legacy_admin.scope = 'legacy'
JOIN auth.users u
    ON u.id = ur.user_id
   AND u.deleted_at IS NULL
CROSS JOIN (SELECT id FROM auth.roles WHERE slug = 'super_admin') AS super_admin
WHERE ur.revoked_at IS NULL
ON CONFLICT (user_id) DO NOTHING;
