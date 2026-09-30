-- An MCP row may connect with a personal API key instead of OAuth.
--
-- WHY
--   Some MCP servers accept a user's own API key as a bearer token (Linear does, at
--   https://mcp.linear.app/mcp). For those, an admin can mark the row oauth_client_source='api_key':
--   each user pastes their key when connecting, and it is stored encrypted in that user's own
--   plugin_connections row exactly where an OAuth access token would be. The catalog row itself
--   holds no credential.
--
-- WHAT CHANGES
--   1. 'api_key' becomes a legal oauth_client_source.
--   2. An api_key row may not carry an OAuth client id or secret. Nothing would ever send them, and a
--      row that looks half-OAuth is how an operator ends up debugging the wrong flow.
--
-- No backfill: every existing row keeps its source.

ALTER TABLE assistant.plugins
    DROP CONSTRAINT IF EXISTS plugins_oauth_client_source_check;

ALTER TABLE assistant.plugins
    ADD CONSTRAINT plugins_oauth_client_source_check
    CHECK (oauth_client_source IN ('unresolved', 'preregistered', 'cimd', 'dcr', 'api_key'));

ALTER TABLE assistant.plugins
    DROP CONSTRAINT IF EXISTS plugins_api_key_forbids_oauth_client;

ALTER TABLE assistant.plugins
    ADD CONSTRAINT plugins_api_key_forbids_oauth_client
    CHECK (
        oauth_client_source <> 'api_key'
        OR (oauth_client_id IS NULL AND oauth_client_secret_encrypted IS NULL)
    );
