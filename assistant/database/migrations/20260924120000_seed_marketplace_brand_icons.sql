-- Give the seeded remote MCP marketplace rows their brand icons.
--
-- WHY
--   20260917130000 seeded Linear, Notion, Atlassian, Asana, monday.com, Canva and Zapier with
--   avatar_url NULL, so every surface that draws a plugin (member Plugins page, workspace Plugins,
--   WarpBot's menus, /admin/plugins) fell back to letter tiles - L, N, AJ, A, M, C, Z - while the
--   Google rows showed their logos. The Google rows point at SVGs the web app serves from
--   /assets/plugins/ (20260907100000); these now point at the same place, where warptalk-web ships
--   the matching files.
--
-- RE-RUNS AND EXISTING ROWS
--   Only a marketplace row (owner_workspace_id IS NULL) with that exact key and NO avatar yet: an
--   icon an admin already set from the catalog page is theirs and is left alone.

UPDATE assistant.plugins AS p
SET avatar_url = icons.avatar_url,
    updated_at = now()
FROM (
    VALUES
        ('linear', '/assets/plugins/linear.svg'),
        ('notion', '/assets/plugins/notion.svg'),
        ('atlassian', '/assets/plugins/atlassian.svg'),
        ('asana', '/assets/plugins/asana.svg'),
        ('monday', '/assets/plugins/monday.svg'),
        ('canva', '/assets/plugins/canva.svg'),
        ('zapier', '/assets/plugins/zapier.svg')
) AS icons (plugin_key, avatar_url)
WHERE p.plugin_key = icons.plugin_key
  AND p.owner_workspace_id IS NULL
  AND (p.avatar_url IS NULL OR btrim(p.avatar_url) = '');
