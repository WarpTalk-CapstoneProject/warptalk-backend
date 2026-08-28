-- WT-565: make a plugin row able to describe a real MCP server, not just a hand-coded provider.
--
-- Until now every plugin needed its own IPluginOAuthClient + IMcpToolGateway implementation in C#,
-- so adding an app meant a deploy. These columns let a row carry everything the generic MCP path
-- needs (server endpoint, discovered OAuth endpoints, dynamically registered client credentials,
-- and a fingerprint of the tool manifest), so adding an app becomes an INSERT.
--
-- `kind` is the dispatch key. It defaults to 'native' so the existing google_workspace row - which
-- has no MCP server and keeps its bespoke gateway - is unaffected by this migration.

ALTER TABLE assistant.plugins
    ADD COLUMN IF NOT EXISTS kind VARCHAR(20) NOT NULL DEFAULT 'native',
    ADD COLUMN IF NOT EXISTS mcp_server_url VARCHAR(1000) NULL,
    -- Cached OAuth discovery (RFC 9728 -> RFC 8414). Null until the first connect attempt runs
    -- discovery; cached afterwards so every tool call does not re-walk the well-known documents.
    ADD COLUMN IF NOT EXISTS oauth_authorization_endpoint VARCHAR(1000) NULL,
    ADD COLUMN IF NOT EXISTS oauth_token_endpoint VARCHAR(1000) NULL,
    ADD COLUMN IF NOT EXISTS oauth_revoke_endpoint VARCHAR(1000) NULL,
    ADD COLUMN IF NOT EXISTS oauth_registration_endpoint VARCHAR(1000) NULL,
    -- Credentials from Dynamic Client Registration (RFC 7591). The secret is protected with the
    -- same IPluginCredentialProtector purpose used for user tokens - never stored in plaintext.
    ADD COLUMN IF NOT EXISTS oauth_client_id VARCHAR(500) NULL,
    ADD COLUMN IF NOT EXISTS oauth_client_secret_encrypted TEXT NULL,
    -- tools_json is authored by us for 'native' rows, but is a *cache* of tools/list for 'mcp'
    -- rows. The hash lets us notice a server changing its tool set behind our back, which clears
    -- any admin approval that downgraded a tool to read-only.
    ADD COLUMN IF NOT EXISTS tools_synced_at TIMESTAMPTZ NULL,
    ADD COLUMN IF NOT EXISTS tools_manifest_hash VARCHAR(128) NULL;

ALTER TABLE assistant.plugins
    DROP CONSTRAINT IF EXISTS plugins_kind_check;

ALTER TABLE assistant.plugins
    ADD CONSTRAINT plugins_kind_check CHECK (kind IN ('native', 'mcp'));

-- An 'mcp' row without a server URL is unusable, and would fail at connect time with a confusing
-- error. Reject it at the boundary instead.
ALTER TABLE assistant.plugins
    DROP CONSTRAINT IF EXISTS plugins_mcp_requires_server_url;

ALTER TABLE assistant.plugins
    ADD CONSTRAINT plugins_mcp_requires_server_url
    CHECK (kind <> 'mcp' OR mcp_server_url IS NOT NULL);
