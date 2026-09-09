-- WT-646 follow-up: the reserved-key CHECK has to be case-insensitive, because the router is.
--
-- WHY
--   20260903120000 and 20260907103000 declare plugins_plugin_key_not_reserved the backstop for
--   "every other write path, including hand-run SQL" -- PluginConstants says the same thing from
--   the application side. It is not one yet. PluginConstants.IsReservedPluginKey compares with
--   StringComparer.OrdinalIgnoreCase; the constraint compares with `plugin_key NOT IN ('mcp',
--   'catalog')`, which is exact. Two enforcement points that are meant to say the same sentence
--   disagree about half the strings they are asked about, and the database is the lenient one --
--   which is the wrong way round for a backstop.
--
--   The gap is reachable, and it is reachable by exactly the write path the constraint is for. An
--   INSERT of plugin_key = 'Catalog' passes the CHECK. ASP.NET route matching is case-insensitive,
--   so GET api/v1/assistant/plugins/Catalog/connection is then matched by
--   AssistantPluginCatalogController's literal `catalog` segment rather than by the {pluginKey}
--   route beside it: an ordinary member asking for their connection status is answered by a
--   system-admin endpoint and gets a 403. That is the precise failure 20260907103000 was written
--   to prevent, arrived at by changing one letter.
--
--   lower() rather than a citext column or a case-insensitive collation: this is one predicate on
--   one column, the application already normalises nothing but case here, and the two heavier
--   options change how plugin_key compares and sorts everywhere else in the schema to fix a
--   two-name list.
--
-- WHY A NEW MIGRATION
--   20260907103000 may already have been applied -- the runner records a checksum per file and
--   fails hard if an applied file's bytes change, so editing it in place would break every
--   environment that has seen it. Rewriting the constraint from here is the forward fix, and
--   DROP + ADD is the same idiom both earlier migrations used.
--
--   Existing rows are re-validated on ADD, which is what we want: if a row keyed 'Catalog' or 'MCP'
--   slipped through the older constraint, this migration fails loudly rather than leaving a row
--   whose own user-facing routes belong to someone else. The forward fix for that is to rename the
--   row -- it is unreachable under that key either way.

ALTER TABLE assistant.plugins
    DROP CONSTRAINT IF EXISTS plugins_plugin_key_not_reserved;

ALTER TABLE assistant.plugins
    ADD CONSTRAINT plugins_plugin_key_not_reserved
    CHECK (lower(plugin_key) NOT IN ('mcp', 'catalog'));
