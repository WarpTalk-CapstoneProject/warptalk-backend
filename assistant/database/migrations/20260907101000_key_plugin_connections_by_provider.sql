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
