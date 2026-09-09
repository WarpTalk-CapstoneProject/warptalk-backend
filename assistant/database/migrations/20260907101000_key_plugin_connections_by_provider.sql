-- WT-646: a plugin_connection is a grant from a provider, not a grant to a plugin.
--
-- WHY
--   20260907100000 split google_workspace into google_drive, google_calendar and google_meet. All
--   three are provider='google', and Calendar and Meet ask for the *same* scope
--   (calendar.events -- a Meet conference is a Calendar event with conferenceData attached, there
--   is no separate Meet scope). With connections keyed UNIQUE (user_id, plugin_id), installing all
--   three would mean three trips through Google's consent screen and three encrypted refresh
--   tokens for one authorisation, each expiring and rotating independently. Revoking one of them
--   at Google's end silently invalidates the other two, because Google revokes per grant, not per
--   token -- so the database would confidently report two connections as healthy while they were
--   already dead.
--
--   The model that matches how the provider actually behaves: a user consents to Google once, and
--   that grant covers whichever Google products they install. Scopes accumulate on it as they
--   install more (OAuth incremental authorisation), and the per-tool scope check that
--   McpToolOrchestrator already performs against scopes_json is what decides whether a given tool
--   may run -- exactly as it did before, just reading one grant instead of three.
--
--   plugin_id survives as provenance: it records which catalog row first sent the user to consent.
--   It is no longer the identity of the connection and must not be looked up by.

-- ---------------------------------------------------------------------------------------------
-- Pre-flight: one provider has to mean one authorization server, or the merge below is not a merge.
--
-- Everything past this point assumes that two connections sharing (user_id, provider) are two
-- records of ONE grant, which is what makes unioning their scopes onto a single row and deleting
-- the rest a consolidation rather than a destruction. That assumption is true of google_drive,
-- google_calendar and google_meet, which 20260907100000 created as three faces of one Google
-- consent. It is not true in general.
--
-- A kind='mcp' row takes its plugin_key as its provider, and every MCP server is its own
-- authorization server with its own grant. PluginInstallationService.CreateMcpPluginAsync now
-- refuses a key that collides with a provider already in the catalog -- but that guard is new in
-- WT-646. On a database that predates it, an operator could have added an MCP row keyed 'google',
-- and there would now be two unrelated plugins claiming provider='google'. The dedupe below would
-- union two different servers' scope strings onto one row and DELETE the other's encrypted refresh
-- token: unrecoverable, and it leaves a row asserting scopes the surviving token was never issued
-- for -- which is worse than either row alone, because nothing downstream can tell that it happened.
--
-- There is no safe automatic answer. Which of the two is "the" google grant is a question about
-- what an operator meant, not about what is in the table, so this refuses to run and names the
-- rows instead. A migration that fails is an afternoon; a migration that succeeds by destroying a
-- token is permanent.
--
-- The forward fix is taken by hand before re-running: give the MCP row a provider of its own
-- (its plugin_key travels with it -- see the guard above), and re-point or delete the connections
-- that were obtained through it. Then this migration has one grant per (user, provider) again and
-- the merge below means what it says.
-- ---------------------------------------------------------------------------------------------
DO $preflight$
DECLARE
    collisions text;
BEGIN
    SELECT string_agg(detail, '; ' ORDER BY detail)
    INTO collisions
    FROM (
        SELECT format(
                   'provider %L is claimed by %s',
                   p.provider,
                   string_agg(format('%s (kind=%s)', p.plugin_key, p.kind), ', ' ORDER BY p.plugin_key)
               ) AS detail
        FROM assistant.plugins AS p
        GROUP BY p.provider
        -- Narrowed to groups containing an MCP row on purpose. Several native rows sharing a
        -- provider is the shape this ticket deliberately creates and must not be refused; an MCP
        -- row sharing one is the shape that cannot be true and safe at the same time.
        HAVING count(*) > 1
           AND count(*) FILTER (WHERE p.kind = 'mcp') > 0
    ) AS grouped;

    IF collisions IS NOT NULL THEN
        RAISE EXCEPTION 'WT-646: refusing to key plugin_connections by provider -- %', collisions
            USING HINT =
                'An MCP plugin''s provider is its own authorization server and cannot be shared. '
                || 'Give the colliding row a provider of its own, deal with the connections '
                || 'obtained through it, then re-run this migration.';
    END IF;
END
$preflight$;

ALTER TABLE assistant.plugin_connections
    ADD COLUMN IF NOT EXISTS provider VARCHAR(100) NULL;

-- Backfill from the catalog row the grant was obtained through. The FK below guarantees that row
-- exists, so this covers every existing connection.
--
-- Strictly a backfill: it touches only rows that have no provider yet. Once provider is the
-- identity of the connection, plugin_id is provenance, and re-deriving one from the other on a
-- re-run would let a stale provenance value overwrite the live answer.
UPDATE assistant.plugin_connections AS c
SET provider = p.provider
FROM assistant.plugins AS p
WHERE p.id = c.plugin_id
  AND c.provider IS NULL;

-- Belt and braces before SET NOT NULL. This can only fire if a row's plugin reference was lost in
-- some earlier hand-repair with the FK disabled; leaving such a row NULL would abort the whole
-- migration on the next statement, which is a worse outcome than parking it under a provider name
-- no OAuth client answers to.
UPDATE assistant.plugin_connections
SET provider = 'unknown'
WHERE provider IS NULL;

-- ---------------------------------------------------------------------------------------------
-- De-duplicate before constraining.
--
-- A user who connected Drive and Calendar separately under the old key now has two rows with the
-- same (user_id, 'google'). One has to win, and the losers' granted scopes have to survive the
-- merge -- otherwise a user who granted calendar.events on one row and drive.readonly on the other
-- comes out of this migration having "lost" a scope they really did grant, and the next Drive call
-- fails with missing_scope even though Google would have honoured it.
--
-- The tie-break, strongest signal first:
--   1. status='connected'. A revoked or expired row is a record of a grant that no longer works.
--      Promoting one over a live grant would break a user who is working fine today.
--   2. encrypted_refresh_token IS NOT NULL. A row without a refresh token stops working the moment
--      its access token expires, and cannot recover without a fresh consent. This outranks
--      recency deliberately: a newer token-less row is worth less than an older refreshable one.
--   3. updated_at DESC, then created_at DESC. Most recently rotated/refreshed.
--   4. id. Not meaningful, present so the order is total and the result is reproducible when two
--      rows tie on everything above.
--
-- Scopes are unioned across the whole group, sorted and de-duplicated, so the winner ends up
-- holding every scope any of the merged rows had recorded.
-- ---------------------------------------------------------------------------------------------
WITH dupes AS (
    SELECT user_id, provider
    FROM assistant.plugin_connections
    GROUP BY user_id, provider
    HAVING count(*) > 1
),
ranked AS (
    SELECT
        c.id,
        c.user_id,
        c.provider,
        row_number() OVER (
            PARTITION BY c.user_id, c.provider
            ORDER BY
                (c.status = 'connected') DESC,
                (c.encrypted_refresh_token IS NOT NULL) DESC,
                c.updated_at DESC,
                c.created_at DESC,
                c.id
        ) AS rn
    FROM assistant.plugin_connections AS c
    JOIN dupes AS d
        ON d.user_id = c.user_id
       AND d.provider = c.provider
),
unioned AS (
    SELECT
        r.user_id,
        r.provider,
        jsonb_agg(DISTINCT scope_row.scope ORDER BY scope_row.scope) AS scopes_json
    FROM ranked AS r
    JOIN assistant.plugin_connections AS c ON c.id = r.id
    CROSS JOIN LATERAL jsonb_array_elements_text(c.scopes_json) AS scope_row(scope)
    GROUP BY r.user_id, r.provider
)
UPDATE assistant.plugin_connections AS winner
SET
    scopes_json = u.scopes_json,
    updated_at = now()
FROM ranked AS r
JOIN unioned AS u
    ON u.user_id = r.user_id
   AND u.provider = r.provider
WHERE winner.id = r.id
  AND r.rn = 1
  -- Nothing to write when the winner already holds the union. Keeps a re-run from churning
  -- updated_at, and keeps this a no-op once the unique constraint below makes duplicates
  -- impossible.
  AND winner.scopes_json IS DISTINCT FROM u.scopes_json;

-- Drop the losers. The ranking is repeated verbatim, and is stable across the statement above:
-- that UPDATE touched only rank-1 rows and only moved their updated_at forward, which is the third
-- sort key and already favoured them. The first two keys were not written at all.
--
-- These rows are deleted, not archived. Their scopes are already merged into the winner above, and
-- what is left on them is an encrypted token for a Google grant that the winner's token refers to
-- as well -- keeping a second encrypted copy of the same authorisation around is a liability, not
-- a backup. This is the one genuinely destructive step in WT-646; take a dump first.
WITH dupes AS (
    SELECT user_id, provider
    FROM assistant.plugin_connections
    GROUP BY user_id, provider
    HAVING count(*) > 1
),
ranked AS (
    SELECT
        c.id,
        row_number() OVER (
            PARTITION BY c.user_id, c.provider
            ORDER BY
                (c.status = 'connected') DESC,
                (c.encrypted_refresh_token IS NOT NULL) DESC,
                c.updated_at DESC,
                c.created_at DESC,
                c.id
        ) AS rn
    FROM assistant.plugin_connections AS c
    JOIN dupes AS d
        ON d.user_id = c.user_id
       AND d.provider = c.provider
)
DELETE FROM assistant.plugin_connections AS c
USING ranked AS r
WHERE c.id = r.id
  AND r.rn > 1;

-- ---------------------------------------------------------------------------------------------
-- NOT NULL, and a temporary server-side default alongside it.
--
-- The default is the expand half of the expand/backfill/contract this column needs; the contract
-- half is a one-line follow-up migration in the NEXT release, not this one.
--
-- Deploys here are migration-first: the SQL runs, then the pods roll. For the length of that roll
-- there are pods serving traffic that were built before this column existed. One of them finishing
-- an OAuth callback INSERTs a connection naming every column it knows about, and provider is not
-- among them -- against a bare NOT NULL with no default that is a 23502. A user who has just
-- consented at Google gets a 500 and the grant is dropped on the floor; they cannot even tell that
-- retrying is the fix.
--
-- The other deploy order is not the escape. Roll the pods first and the new code SELECTs a column
-- that does not exist yet, so every connection read fails for everyone until the SQL lands.
-- Migration-first is the only order with a working state at both ends, and the default is what
-- makes the middle survivable.
--
-- 'unknown' is the value on purpose: it is the same one the belt-and-braces UPDATE above parks an
-- orphaned row under, and it is a provider name no OAuth client answers to. A row written during
-- the window is therefore inert rather than wrong. The new code looks a connection up by provider,
-- so it never finds it, the user is shown as not connected, and consenting again writes a correct
-- row -- which succeeds, because the new unique key is (user_id, provider) and 'unknown' collides
-- with nothing.
--
-- Deliberately not something cleverer. A trigger deriving provider from plugin_id would guess a
-- real provider for a row an old pod wrote, and a plausible-looking row is worse than an inert
-- one: under the new unique key it would collide with the user's live Google grant, turning a 500
-- at consent time into a 23505 at consent time, having also minted a second encrypted token for a
-- grant that already had one.
--
-- CONTRACT, next release:
--     ALTER TABLE assistant.plugin_connections ALTER COLUMN provider DROP DEFAULT;
-- once no pre-WT-646 pod is left, so that a write path forgetting provider fails loudly again.
-- It cannot ship in this release: it would run in the same migration pass, before the pods roll,
-- and close the window it exists for. Until it lands,
--     SELECT * FROM assistant.plugin_connections WHERE provider = 'unknown'
-- is the exact list of rows the window produced, and the only rows the contract has to look at.
-- ---------------------------------------------------------------------------------------------
ALTER TABLE assistant.plugin_connections
    ALTER COLUMN provider SET DEFAULT 'unknown';

ALTER TABLE assistant.plugin_connections
    ALTER COLUMN provider SET NOT NULL;

-- (user_id, plugin_id) stops being the identity of a connection. Dropping it also drops the index
-- behind it; the new unique constraint indexes the lookup that replaces it, and the FK gets its
-- own index below.
ALTER TABLE assistant.plugin_connections
    DROP CONSTRAINT IF EXISTS plugin_connections_user_plugin_id_key;

-- UNIQUE has no IF NOT EXISTS, so drop-then-add is the idempotent form (same idiom as
-- 20260828100000). Safe to repeat: the de-duplication above has already made the data satisfy it.
ALTER TABLE assistant.plugin_connections
    DROP CONSTRAINT IF EXISTS plugin_connections_user_provider_key;

ALTER TABLE assistant.plugin_connections
    ADD CONSTRAINT plugin_connections_user_provider_key UNIQUE (user_id, provider);

-- ---------------------------------------------------------------------------------------------
-- The FK cascade is now a bug waiting to happen.
--
-- plugin_id used to identify the connection, so ON DELETE CASCADE read correctly: delete the
-- plugin, delete its connections. Now one connection outlives any single plugin row -- deleting
-- google_drive from the catalog would cascade away the shared Google grant and disconnect that
-- user's Calendar and Meet as well, destroying an encrypted refresh token that had nothing to do
-- with Drive.
--
-- ON DELETE SET NULL was the other candidate and is rejected here: plugin_id is NOT NULL, and
-- making it nullable is a change the AssistantService entity mapping would have to follow in the
-- same deploy, which this migration cannot coordinate.
--
-- RESTRICT it is. It encodes the policy 20260907100000 already followed by hand when it
-- deactivated google_workspace instead of deleting it: a catalog row that anyone has ever
-- connected through is retired with is_active=false, never DELETEd. An operator who really means
-- to delete one has to deal with its connections first, explicitly.
--
-- plugin_installations keeps its cascade untouched -- an installation genuinely is per-plugin, and
-- deleting a plugin should take its installations with it.
-- ---------------------------------------------------------------------------------------------
ALTER TABLE assistant.plugin_connections
    DROP CONSTRAINT IF EXISTS plugin_connections_plugin_id_fkey;

ALTER TABLE assistant.plugin_connections
    ADD CONSTRAINT plugin_connections_plugin_id_fkey FOREIGN KEY (plugin_id)
        REFERENCES assistant.plugins (id) ON DELETE RESTRICT;

-- RESTRICT makes every plugin delete scan plugin_connections for referencing rows, and the index
-- that used to serve that scan went away with the old unique constraint.
CREATE INDEX IF NOT EXISTS idx_plugin_connections_plugin_id
    ON assistant.plugin_connections (plugin_id);
