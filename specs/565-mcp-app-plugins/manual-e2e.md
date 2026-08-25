# Manual E2E: Google Drive & Calendar MCP Plugin

Use this checklist for T036 after a real Google OAuth client is configured for local Gateway -> AssistantService routing.

## Required Local Configuration

- PostgreSQL is running and AssistantService migrations have been applied.
- AssistantService is configured with:
  - `Plugins:GoogleWorkspace:OAuth:ClientId`
  - `Plugins:GoogleWorkspace:OAuth:ClientSecret`
  - `Plugins:GoogleWorkspace:OAuth:RedirectUri=http://localhost:5200/api/v1/assistant/plugins/google_workspace/oauth/callback`
- Google OAuth client allows `http://localhost:5200/api/v1/assistant/plugins/google_workspace/oauth/callback`.
- The test Google account has access to at least one Drive file and one Calendar.

## Flow

1. Sign in to WarpTalk as account A.
2. Open Personal Settings -> Plugins.
3. Install Google Drive & Calendar.
4. Click Connect and complete Google OAuth in the browser.
5. Return to Plugins and verify Google Drive & Calendar shows connected with account A's provider email.
6. Open WarpBot on `/ai-chat` and ask it to search Google Drive for an existing file.
7. Verify WarpBot answers from provider-backed Drive results.
8. Ask WarpBot to open or read one known Drive file from the search result.
9. Verify WarpBot answers from `google_drive_get_file` using sanitized metadata and bounded text content, or gives an explicit unsupported/too-large response for unsupported files.
10. Ask WarpBot to create a Google Calendar event.
11. Verify WarpBot renders a confirmation card and does not create the event before confirmation.
12. Confirm the action.
13. Verify the Calendar event exists in Google Calendar.
14. Sign in as account B in the same workspace.
15. Verify account B does not inherit account A's plugin installation or connection.
16. Turn workspace `AllowAnyPlugins` off as Owner/Admin.
17. Verify WarpBot does not expose/invoke personal plugin tools in that workspace.

## Expected Result

- Personal plugin installation and connection are scoped to the signed-in account.
- Workspace policy only gates whether WarpBot can invoke personal plugins in that workspace.
- Provider writes require confirmation on both `/ai-chat` and room chat surfaces.
- Full provider scope is covered: Drive search, Drive get/read file, Calendar list/create.
