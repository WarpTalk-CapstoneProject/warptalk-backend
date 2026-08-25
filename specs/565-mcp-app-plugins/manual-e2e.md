# Manual E2E: Google Workspace MCP Plugins

Use this checklist for T036 after a real Google OAuth client is configured for local AssistantService.

## Required Local Configuration

- PostgreSQL is running and AssistantService migrations have been applied.
- AssistantService is configured with:
  - `GoogleWorkspace:OAuth:ClientId`
  - `GoogleWorkspace:OAuth:ClientSecret`
  - `GoogleWorkspace:OAuth:RedirectUri`
- Google OAuth client allows the configured redirect URI.
- The test Google account has access to at least one Drive file and one Calendar.

## Flow

1. Sign in to WarpTalk as account A.
2. Open Personal Settings -> Plugins.
3. Install Google Workspace.
4. Click Connect and complete Google OAuth in the browser.
5. Return to Plugins and verify Google Workspace shows connected with account A's provider email.
6. Open WarpBot on `/ai-chat` and ask it to search Google Drive for an existing file.
7. Verify WarpBot answers from provider-backed Drive results.
8. Ask WarpBot to create a Google Calendar event.
9. Verify WarpBot renders a confirmation card and does not create the event before confirmation.
10. Confirm the action.
11. Verify the Calendar event exists in Google Calendar.
12. Sign in as account B in the same workspace.
13. Verify account B does not inherit account A's plugin installation or connection.
14. Turn workspace `AllowAnyPlugins` off as Owner/Admin.
15. Verify WarpBot does not expose/invoke personal plugin tools in that workspace.

## Expected Result

- Personal plugin installation and connection are scoped to the signed-in account.
- Workspace policy only gates whether WarpBot can invoke personal plugins in that workspace.
- Provider writes require confirmation on both `/ai-chat` and room chat surfaces.
