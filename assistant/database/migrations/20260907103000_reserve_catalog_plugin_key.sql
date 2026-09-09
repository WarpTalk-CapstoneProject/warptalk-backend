-- WT-646: 'catalog' joins 'mcp' as a plugin key no row may use.
--
-- 20260903120000 reserved 'mcp' because the shared MCP OAuth callback lives at the literal segment
-- assistant/plugins/mcp/oauth/callback, and ASP.NET routing gives a literal precedence over the
-- {pluginKey} route beside it. The admin catalog surface added by this ticket does the same thing
-- one level up: assistant/plugins/catalog/{pluginKey} is two segments deep under the same prefix
-- as the user-facing assistant/plugins/{pluginKey}/connection and .../connect-url.
--
-- Nothing about that is ambiguous to the router — the literal simply wins. The damage is to a row
-- that legitimately wanted the key: every user-facing two-segment call for a plugin keyed
-- 'catalog' would be answered by a system-admin endpoint, so an ordinary member asking for their
-- connection status would get a 403. Not a broken route; a route that quietly belongs to someone
-- else. Reject the name rather than leave that to be discovered in production.
--
-- The application enforces the same list in PluginConstants.ReservedPluginKeys, consulted by
-- CreateMcpPluginAsync so an operator gets a sentence instead of a constraint-violation stack
-- trace. This constraint is the backstop for every other write path, including hand-run SQL.
--
-- Rewritten rather than extended: a CHECK cannot be altered in place, and DROP + ADD is the same
-- idiom 20260903120000 used. Existing rows are re-validated on ADD, which is what we want — if a
-- row keyed 'catalog' somehow exists, this migration should fail loudly rather than leave it.

ALTER TABLE assistant.plugins
    DROP CONSTRAINT IF EXISTS plugins_plugin_key_not_reserved;

ALTER TABLE assistant.plugins
    ADD CONSTRAINT plugins_plugin_key_not_reserved
    CHECK (plugin_key NOT IN ('mcp', 'catalog'));
