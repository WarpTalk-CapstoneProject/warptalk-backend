-- Backfill: make each workspace's policy columns say what the workspace actually is.
--
-- WHY ANY ROW IS WRONG
--   WorkspaceMapper.ToEntity built the Workspace entity without assigning
--   require_verified_domain_for_internal or allow_external_collaboration, so every workspace ever
--   created through the API took the CLR default false for both — regardless of what the caller
--   asked for, and regardless of what CreateWorkspaceAsync wrote into the settings JSON alongside
--   it. Those two columns, not the JSON, are what WorkspaceHelper.GetWorkspaceConfig reads as
--   policy.
--
--   The web client has always sent requireVerifiedDomainForInternal: true and claimed the
--   founder's email domain. So workspaces exist right now that hold verified domain rows, show
--   those domains in Settings, and are running with no domain policy at all: invitation
--   validation skips the domain check, membership classification hands out Internal to any
--   address, and the one-internal-home-per-user rule does not count them.
--
-- PART 1 — require_verified_domain_for_internal
--   Derived, not configured: a workspace requires a verified domain exactly when it holds one.
--   That is the invariant the code now maintains (WorkspaceHelper.RecomputeDomainPolicyAsync),
--   and this is the same expression applied once to existing rows.
--
--   THIS CHANGES BEHAVIOUR ON REAL DATA. A workspace flipped to true starts enforcing the domain
--   rule on new invitations and join requests. Existing members are NOT reclassified — they keep
--   the MembershipType they were granted — but some of them will be Internal on a domain the
--   workspace does not verify. That is intentional: reclassifying members retroactively would
--   revoke access nobody asked to revoke. Run the DRY RUN below first and tell the affected
--   owners before applying.
--
-- PART 2 — allow_external_collaboration
--   Narrower, and not derived from anything: it is a genuine setting whose column simply never
--   received the value. Only rows where the column contradicts the workspace's own settings JSON
--   are touched, so a workspace whose owner deliberately turned external collaboration off — via
--   Settings, which does write the column — is left alone.
--
-- Idempotent and forward-only: re-running changes nothing once the columns agree.

-- ─────────────────────────────────────────────────────────────────────────────
-- DRY RUN — run these two SELECTs first. Neither modifies anything.
-- ─────────────────────────────────────────────────────────────────────────────
--
-- 1. Which workspaces change policy, and how exposed each one is:
--
--   SELECT w.id,
--          w.name,
--          w.require_verified_domain_for_internal AS current_flag,
--          count(DISTINCT vd.id) FILTER (WHERE vd.status = 'verified' AND vd.revoked_at IS NULL)
--            AS active_domains
--   FROM workspace.workspaces w
--   LEFT JOIN workspace.workspace_verified_domains vd ON vd.workspace_id = w.id
--   WHERE w.deleted_at IS NULL
--   GROUP BY w.id, w.name, w.require_verified_domain_for_internal
--   HAVING w.require_verified_domain_for_internal
--          <> (count(DISTINCT vd.id) FILTER (WHERE vd.status = 'verified' AND vd.revoked_at IS NULL) > 0);
--
-- 2. Internal members who will no longer satisfy the policy their workspace is about to enforce.
--    They keep their access; this is the list to warn owners about, not a list of rows to change.
--    (Requires reading member emails from the auth service — cannot be joined here, since each
--    service owns a separate logical database. Take the workspace ids from query 1 and resolve
--    member emails through the admin API.)

-- ─────────────────────────────────────────────────────────────────────────────
-- PART 1
-- ─────────────────────────────────────────────────────────────────────────────

UPDATE workspace.workspaces AS w
SET require_verified_domain_for_internal = derived.has_active_domain,
    updated_at = NOW()
FROM (
    SELECT w2.id,
           EXISTS (
               SELECT 1
               FROM workspace.workspace_verified_domains vd
               WHERE vd.workspace_id = w2.id
                 AND vd.status = 'verified'
                 AND vd.verified_at IS NOT NULL
                 AND vd.revoked_at IS NULL
           ) AS has_active_domain
    FROM workspace.workspaces w2
    WHERE w2.deleted_at IS NULL
) AS derived
WHERE w.id = derived.id
  AND w.require_verified_domain_for_internal <> derived.has_active_domain;

-- ─────────────────────────────────────────────────────────────────────────────
-- PART 2
-- ─────────────────────────────────────────────────────────────────────────────

UPDATE workspace.workspaces
SET allow_external_collaboration = true,
    updated_at = NOW()
WHERE deleted_at IS NULL
  AND allow_external_collaboration = false
  AND COALESCE(settings ->> 'AllowExternalCollaboration', 'true') = 'true';

-- ─────────────────────────────────────────────────────────────────────────────
-- Deleted workspaces release their domains (SoftDeleteWorkspaceAsync now does this going
-- forward; rows deleted before that change still hold their claims, and nobody is left who
-- could revoke them — the workspace is gone).
-- ─────────────────────────────────────────────────────────────────────────────

UPDATE workspace.workspace_verified_domains AS vd
SET status = 'revoked',
    revoked_at = NOW(),
    updated_at = NOW()
FROM workspace.workspaces AS w
WHERE vd.workspace_id = w.id
  AND w.deleted_at IS NOT NULL
  AND vd.revoked_at IS NULL;
