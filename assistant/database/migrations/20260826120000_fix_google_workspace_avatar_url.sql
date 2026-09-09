-- The seeded avatar_url pointed at
-- https://www.gstatic.com/images/branding/product/2x/workspace_48dp.png, which now 404s
-- (Google retired the asset). PluginGlyph's onError fallback was doing exactly what it was
-- built to do — show initials once the image genuinely fails to load — so the plugins page
-- always rendered "GD" instead of an icon. Point at an icon the web app serves itself instead
-- of a third-party host that can move or disappear without warning.
UPDATE assistant.plugins
SET avatar_url = '/assets/plugins/google-drive-calendar.svg'
WHERE plugin_key = 'google_workspace';
