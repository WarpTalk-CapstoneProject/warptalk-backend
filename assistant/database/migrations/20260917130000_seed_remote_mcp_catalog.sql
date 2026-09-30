-- Seed the marketplace with the remote MCP apps that connect without operator work.
--
-- WHY
--   Until now a deployed catalog held only the three Google rows a migration wrote; every other app
--   came from specs/565-mcp-app-plugins/local-e2e/seed-catalog.sh, which needs a platform-admin JWT
--   and was only ever run against a local stack. Members on a deployed environment therefore saw a
--   three-row catalog.
--
-- WHICH ROWS
--   Only servers that connect with nothing configured: dynamic client registration (or CIMD) lets
--   the WT-602 ladder resolve the OAuth client on the first connect, so these rows are written
--   'unresolved', exactly as POST /plugins/catalog writes them (McpPluginRows.Build).
--
--   Linear is the exception: it is seeded 'api_key', so each user pastes a personal API key. Linear
--   accepts `Authorization: Bearer <key>` on its MCP endpoint. An admin can switch it to OAuth from
--   the catalog page.
--
--   Left out on purpose: GitHub, Slack and HubSpot (their authorization servers support neither DCR
--   nor CIMD) and Figma (its registration endpoint refuses every registration). Seeded, they would
--   only ever show "needs operator setup".
--
-- RE-RUNS AND EXISTING ROWS
--   ON CONFLICT (plugin_key) DO NOTHING: a row an admin already created, edited or retired keeps
--   whatever the admin chose. tools_json starts empty and fills on the first connect.

INSERT INTO assistant.plugins (
    id, plugin_key, label, description, avatar_url, provider, required_scopes_json, tools_json,
    kind, mcp_server_url, oauth_client_source, is_active, is_featured, sort_order, category,
    created_at, updated_at
)
VALUES
    ('5e0a8f1c-2b7d-4c3e-9a61-0d4b7c2e8f01'::uuid, 'linear', 'Linear',
     'Turn action items into Linear issues, and look up projects and comments.',
     NULL, 'linear', '[]'::jsonb, '[]'::jsonb, 'mcp', 'https://mcp.linear.app/mcp', 'api_key',
     true, false, 100, NULL, now(), now()),
    ('5e0a8f1c-2b7d-4c3e-9a61-0d4b7c2e8f02'::uuid, 'notion', 'Notion',
     'Search pages and write meeting notes in your Notion workspace.',
     NULL, 'notion', '[]'::jsonb, '[]'::jsonb, 'mcp', 'https://mcp.notion.com/mcp', 'unresolved',
     true, false, 110, NULL, now(), now()),
    ('5e0a8f1c-2b7d-4c3e-9a61-0d4b7c2e8f03'::uuid, 'atlassian', 'Atlassian Jira & Confluence',
     'Search and update Jira issues and Confluence pages in your Atlassian site.',
     NULL, 'atlassian', '[]'::jsonb, '[]'::jsonb, 'mcp', 'https://mcp.atlassian.com/v1/mcp', 'unresolved',
     true, false, 120, NULL, now(), now()),
    ('5e0a8f1c-2b7d-4c3e-9a61-0d4b7c2e8f04'::uuid, 'asana', 'Asana',
     'Find and manage Asana tasks and projects.',
     NULL, 'asana', '[]'::jsonb, '[]'::jsonb, 'mcp', 'https://mcp.asana.com/mcp', 'unresolved',
     true, false, 130, NULL, now(), now()),
    ('5e0a8f1c-2b7d-4c3e-9a61-0d4b7c2e8f05'::uuid, 'monday', 'monday.com',
     'Read and update boards and items in monday.com.',
     NULL, 'monday', '[]'::jsonb, '[]'::jsonb, 'mcp', 'https://mcp.monday.com/mcp', 'unresolved',
     true, false, 140, NULL, now(), now()),
    ('5e0a8f1c-2b7d-4c3e-9a61-0d4b7c2e8f06'::uuid, 'canva', 'Canva',
     'Search and create Canva designs.',
     NULL, 'canva', '[]'::jsonb, '[]'::jsonb, 'mcp', 'https://mcp.canva.com/mcp', 'unresolved',
     true, false, 150, NULL, now(), now()),
    ('5e0a8f1c-2b7d-4c3e-9a61-0d4b7c2e8f07'::uuid, 'zapier', 'Zapier',
     'Run Zapier actions across thousands of connected apps.',
     NULL, 'zapier', '[]'::jsonb, '[]'::jsonb, 'mcp', 'https://mcp.zapier.com/api/mcp/mcp', 'unresolved',
     true, false, 160, NULL, now(), now())
ON CONFLICT (plugin_key) DO NOTHING;
