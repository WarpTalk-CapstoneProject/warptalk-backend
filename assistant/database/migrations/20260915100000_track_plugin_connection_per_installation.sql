-- A plugin is connected when the user connected IT, not whenever its provider's grant happens to
-- cover its scopes.
--
-- WHY
--   20260907101000 keyed plugin_connections by (user_id, provider): one Google grant backs
--   google_drive, google_calendar and google_meet. Every surface then read "connected" straight off
--   that grant, so connecting Calendar switched Meet on too (both need calendar.events), and Drive
--   as well whenever Google handed back a previously granted scope through include_granted_scopes.
--   The user connected one plugin and watched three turn on.
--
--   The grant stays shared - that is what spares the user a second trip through Google's consent.
--   What moves to the installation is the decision: connected_at is stamped when the user connects
--   that plugin and cleared when they disconnect or remove it.
--
-- BACKFILL
--   Existing connections predate the column, so it has to be reconstructed without inventing
--   choices the user never made:
--
--   1. The row a connection was obtained through (plugin_connections.plugin_id, the provenance
--      20260907101000 kept) is connected. The user demonstrably connected that one.
--   2. When that provenance row is not a live installation any more - the retired google_workspace
--      row is the case that exists - there is no single row to credit, so every installed, active
--      row of the provider whose required scopes the grant covers is kept connected. That is exactly
--      what those users saw before this migration, so nothing they rely on stops working.
--
--   A sibling left unconnected by this is one click away: with the grant already covering it,
--   Connect links it without leaving WarpTalk.

ALTER TABLE assistant.plugin_installations
    ADD COLUMN IF NOT EXISTS connected_at TIMESTAMPTZ NULL;

-- 1. The provenance row.
UPDATE assistant.plugin_installations AS i
SET connected_at = COALESCE(c.token_rotated_at, c.updated_at, now())
FROM assistant.plugin_connections AS c
WHERE c.user_id = i.user_id
  AND c.plugin_id = i.plugin_id
  AND c.status = 'connected'
  AND i.status = 'installed'
  AND i.connected_at IS NULL;

-- 2. No live provenance row: keep what the grant already covered.
UPDATE assistant.plugin_installations AS i
SET connected_at = COALESCE(c.token_rotated_at, c.updated_at, now())
FROM assistant.plugin_connections AS c
JOIN assistant.plugins AS p
    ON p.provider = c.provider
   AND p.is_active
WHERE i.plugin_id = p.id
  AND i.user_id = c.user_id
  AND i.status = 'installed'
  AND i.connected_at IS NULL
  AND c.status = 'connected'
  AND c.scopes_json @> p.required_scopes_json
  AND NOT EXISTS (
      SELECT 1
      FROM assistant.plugin_installations AS provenance
      JOIN assistant.plugins AS provenance_plugin
          ON provenance_plugin.id = provenance.plugin_id
      WHERE provenance.user_id = c.user_id
        AND provenance.plugin_id = c.plugin_id
        AND provenance.status = 'installed'
        AND provenance_plugin.is_active
  );
