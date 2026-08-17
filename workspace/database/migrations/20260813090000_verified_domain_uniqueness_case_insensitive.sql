-- Make "one workspace per verified domain" hold regardless of letter case.
--
-- WHAT IS WRONG TODAY
--   idx_workspace_verified_domains_unique_verified is declared on (domain), so Postgres compares
--   the raw string. ACME.com and acme.com are two different values to it, and two workspaces can
--   each hold one of them — while every membership decision downstream lowercases the domain
--   before comparing, and would treat both as the same company.
--
--   The reason this has not bitten yet is that both write paths happen to normalise first
--   (VerifiedDomainService and WorkspaceService both .ToLowerInvariant() the domain). That is an
--   application-layer habit standing in for a database constraint: any other writer — a seed
--   script, a data import, a future service, a hand-run UPDATE — bypasses it entirely. The rule
--   this index exists to enforce is a data rule, so it belongs in the data.
--
-- WHY THE CHECK BLOCK IS NOT OPTIONAL
--   CREATE UNIQUE INDEX fails outright if the table already holds two verified rows whose domains
--   differ only by case. That is not a migration problem to work around — it means two workspaces
--   are both claiming one company's domain right now, and which of them should keep it is a
--   business decision nobody can make from inside a migration. So: detect, refuse, and report the
--   exact rows, rather than picking a winner silently or leaving the index uncreated.
--
-- Idempotent and forward-only: re-running finds the new index already present and does nothing.

DO $$
DECLARE
    conflicting text;
BEGIN
    SELECT string_agg(format('%s (%s workspaces)', d, n), ', ')
    INTO conflicting
    FROM (
        SELECT lower(domain) AS d, count(DISTINCT workspace_id) AS n
        FROM workspace.workspace_verified_domains
        WHERE status = 'verified'
        GROUP BY lower(domain)
        HAVING count(DISTINCT workspace_id) > 1
    ) AS dupes;

    IF conflicting IS NOT NULL THEN
        RAISE EXCEPTION
            'Cannot enforce case-insensitive verified-domain uniqueness: % already claimed by more than one workspace. Decide which workspace keeps each domain and revoke the others (set status/revoked_at) before re-running.',
            conflicting;
    END IF;
END $$;

-- Normalise what is already stored, so the table reads the way every consumer already assumes.
-- Runs before the index so a row like 'ACME.com' cannot collide with itself on creation.
UPDATE workspace.workspace_verified_domains
SET domain = lower(domain),
    updated_at = NOW()
WHERE domain <> lower(domain);

DROP INDEX IF EXISTS workspace.idx_workspace_verified_domains_unique_verified;

CREATE UNIQUE INDEX IF NOT EXISTS idx_workspace_verified_domains_unique_verified
ON workspace.workspace_verified_domains (lower(domain))
WHERE status = 'verified';
