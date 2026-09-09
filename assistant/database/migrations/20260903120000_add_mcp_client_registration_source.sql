-- WT-602: record which client-registration mechanism a plugin row settled on.
--
-- MCP Authorization 2026-07-28 defines three mechanisms in a fixed priority order - pre-registered
-- client information, Client ID Metadata Documents, then Dynamic Client Registration (which the
-- spec marks deprecated, retained only for authorization servers without CIMD). A client walks
-- that ladder; it does not pick one.
--
-- Without a column recording where a row landed, the ladder re-derives the answer on every connect
-- and can contradict itself. That is the shape of anthropics/claude-code#67258: a row that already
-- carried a usable client id still fell through to dynamic registration and failed, because
-- nothing had recorded that the question was already settled.
--
-- 'unresolved' is the default: discovery has not run yet, and the ladder will choose on first
-- connect. An operator installing a server that needs pre-registration sets 'preregistered' and
-- supplies the client id in the same insert.

ALTER TABLE assistant.plugins
    ADD COLUMN IF NOT EXISTS oauth_client_source VARCHAR(20) NOT NULL DEFAULT 'unresolved',
    -- Cached from the authorization server's metadata (RFC 8414 / OIDC discovery) so the ladder
    -- does not re-walk the well-known documents on every connect.
    ADD COLUMN IF NOT EXISTS oauth_cimd_supported BOOLEAN NULL,
    -- RFC 9207: whether the server advertises the `iss` authorization-response parameter. Drives
    -- whether a missing `iss` is a rejection or is allowed to proceed.
    ADD COLUMN IF NOT EXISTS oauth_iss_parameter_supported BOOLEAN NULL,
    -- The token-endpoint authentication method actually negotiated, not the one we asked for.
    -- A CIMD client cannot use a shared secret, so it lands on 'private_key_jwt' where the server
    -- accepts it and 'none' where it does not. Recording it keeps that downgrade visible instead
    -- of silent.
    ADD COLUMN IF NOT EXISTS oauth_token_endpoint_auth_method VARCHAR(40) NULL;

ALTER TABLE assistant.plugins
    DROP CONSTRAINT IF EXISTS plugins_oauth_client_source_check;

ALTER TABLE assistant.plugins
    ADD CONSTRAINT plugins_oauth_client_source_check
    CHECK (oauth_client_source IN ('unresolved', 'preregistered', 'cimd', 'dcr'));

-- A row claiming pre-registration with no client id is unusable, and would fail at connect time
-- with a confusing error. Reject it at the boundary instead.
ALTER TABLE assistant.plugins
    DROP CONSTRAINT IF EXISTS plugins_preregistered_requires_client_id;

ALTER TABLE assistant.plugins
    ADD CONSTRAINT plugins_preregistered_requires_client_id
    CHECK (oauth_client_source <> 'preregistered' OR oauth_client_id IS NOT NULL);

-- Mirrors the CIMD rule enforced by every conformant authorization server: a client identified by
-- a public metadata document can never hold a shared secret. Storing one would mean we intended to
-- send it, which the server would reject anyway.
ALTER TABLE assistant.plugins
    DROP CONSTRAINT IF EXISTS plugins_cimd_forbids_client_secret;

ALTER TABLE assistant.plugins
    ADD CONSTRAINT plugins_cimd_forbids_client_secret
    CHECK (oauth_client_source <> 'cimd' OR oauth_client_secret_encrypted IS NULL);

-- google_workspace is kind='native' with its own hand-written OAuth client reading credentials
-- from configuration, so it never walks the ladder. Leave it 'unresolved'.

-- Every kind='mcp' plugin shares one OAuth redirect URI, /api/v1/assistant/plugins/mcp/oauth/
-- callback, because a Client ID Metadata Document must enumerate its redirect URIs exactly and
-- servers cache that document for as long as a week. ASP.NET routing gives that literal segment
-- precedence over the {pluginKey} route beside it, so a row keyed 'mcp' would silently shadow the
-- shared callback and break every MCP connection. Reject the name rather than leave a landmine.
ALTER TABLE assistant.plugins
    DROP CONSTRAINT IF EXISTS plugins_plugin_key_not_reserved;

ALTER TABLE assistant.plugins
    ADD CONSTRAINT plugins_plugin_key_not_reserved
    CHECK (plugin_key <> 'mcp');
