# Manual E2E: Google Drive & Calendar MCP Plugin

Use this checklist for T036 after a real Google OAuth client is configured for local Gateway -> AssistantService routing.

## Required Local Configuration

- Set `BACKEND_SOURCE_ROOT=../_worktrees/wt-565-backend` in the infrastructure `.env` before
  running the compose stack, so containers include the feature implementation and migrations:
  `docker compose -f docker-compose.yml -f docker-compose.dev.yml up --build`.
  The infrastructure compose default remains `../warptalk-backend` for normal development.
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
   - Local URL: `http://localhost:3000/settings/plugins`.
   - The old workspace-shaped URL `/:workspaceSlug/settings/plugins` is only a legacy redirect to the personal route.
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
14. Force account A's `google_workspace` connection to `expired`.
15. Ask global WarpBot to search Google Drive again.
16. Verify WarpBot renders an in-chat plugin action card with Google Drive & Calendar, `Not now`,
    and `Reconnect`.
17. Click `Not now`; verify the card dismisses locally and the backend connection row is unchanged.
18. Ask again and click `Reconnect`; verify the Google OAuth URL opens in the browser.
19. Repeat the expired-connection card check in room chat.
20. Sign in as account B in the same workspace.
21. Verify account B does not inherit account A's plugin installation or connection.
22. Turn workspace `AllowAnyPlugins` off as Owner/Admin.
23. Verify WarpBot does not expose/invoke personal plugin tools in that workspace and does not show
    a reconnect card, because workspace policy is not fixed by provider OAuth.

## Expected Result

- Personal plugin installation and connection are scoped to the signed-in account.
- Workspace policy only gates whether WarpBot can invoke personal plugins in that workspace.
- Provider writes require confirmation on both `/ai-chat` and room chat surfaces.
- Expired, revoked, or missing personal provider connections recover in the chat window through a
  connect/reconnect action card.
- Full provider scope is covered: Drive search, Drive get/read file, Calendar list/create.

## Local Evidence Log

- 2026-08-26: Docker/Postgres stack rebuilt with `BACKEND_SOURCE_ROOT=../_worktrees/wt-565-backend` and `FRONTEND_SOURCE_ROOT=../_worktrees/wt-565-web`.
- 2026-08-26: Google OAuth callback through gateway `:5200` returned `status=connected` for provider email `hanhnhi10022005@gmail.com`.
- 2026-08-26: Frontend rebuilt after moving Plugins to `/settings/plugins`; `npm run typecheck`, Docker `next build`, and `git diff --check` passed.
- 2026-08-26: Fixed compose AssistantService workspace policy gRPC URL to `http://workspace-service:50056`; `GET /api/v1/assistant/mcp/tools?workspaceId=019f0d00-0de0-7000-9000-0000000000aa` now returns all four tools when `allowAnyPlugins=true`.
- 2026-08-26: Verified workspace policy gate through gateway: before `allowAnyPlugins=true` -> 4 tools; patched `allowAnyPlugins=false` -> 0 tools; restored `allowAnyPlugins=true` -> 4 tools.
- 2026-08-26: Verified account isolation through gateway: `mentor.demo@warptalk.vn` has `google_workspace` installed/connected, while `hanhnhi.demo@warptalk.vn` in the same demo workspace sees `google_workspace` as `not_installed`/`not_connected`.
- 2026-08-26: `google_drive_search` through gateway returned `connection_required` and marked the connection expired. The local DB had no stored refresh token for the prior Google consent, so provider-tool E2E requires a fresh Google reconnect; if Google still omits a refresh token, revoke the app grant from the Google account and consent again.
- 2026-08-26: Quality checks after the compose policy fix and personal plugin route refactor: AssistantService plugin tests `56/56`, web `npm run typecheck`, plugin marketplace contract, confirmation-surface contract, infra `docker compose config --quiet`, and `git diff --check` passed.
- 2026-08-27: Implemented in-chat plugin connection action card contract. AssistantService plugin tests `66/66`, AI targeted tests `52/52`, web `npm run typecheck`, plugin marketplace contract, confirmation-surface contract, plugin-connection-action contract, and `git diff --check` across backend/web/AI passed. Frontend Docker image and AI assistant Docker image built; AI smoke container booted and was removed. AssistantService local Release publish passed; AssistantService Docker restore remained very slow/stalled and still needs a clean Docker image result.
- 2026-08-27: Rebuilt the AssistantService runtime smoke image from local Release publish and restarted compose. Through gateway `:5200`, `GET /api/v1/assistant/mcp/tools` returned all four tools. `google_drive_search` with query `WarpTalk` returned real Google Drive files. `google_drive_get_file` returned bounded inline content for `publish_may_cham.txt` (`contentStatus=available`) and explicit unsupported metadata for Google Sheets/octet-stream files.
- 2026-08-27: Calendar create without confirmation returned `confirmation_required` with a token and a follow-up list check showed zero matching events before confirmation. Confirming the write did not create the event because Google Calendar provider requests currently return Google reason `accessNotConfigured`; the gateway now classifies that as `provider_unavailable` instead of incorrectly telling the user to reconnect for scopes. Next external action: enable Google Calendar API on the OAuth client project, then re-run Calendar list/create.
- 2026-08-27: Re-verified reconnect metadata by setting account A's Google connection to `expired`, calling `google_drive_search`, and restoring the row to `connected`. The gateway response was `connection_required` with `pluginKey=google_workspace`, `pluginLabel=Google Drive & Calendar`, `connectionStatus=expired`, and connected account email.
- 2026-08-27: Re-verified workspace and account boundaries: `AllowAnyPlugins=false` returned zero tools and execution returned `permission_denied` with reconnect metadata null; restoring `AllowAnyPlugins=true` returned four tools. Account A sees Google Drive & Calendar installed/connected, while account B in the same workspace sees `not_installed`/`not_connected`.
- 2026-08-27: Diagnosed AssistantService Docker restore separately from compose. A direct SDK container restore with the host NuGet cache mounted succeeds, so the Linux project graph is valid. A minimal Docker restore harness with verbose output shows the unseeded path is downloading packages very slowly from NuGet rather than deadlocking (`Microsoft.EntityFrameworkCore.Design` index took about 67s) and timed out after 180s while still progressing. A container-only EF design-time exclusion would reduce the graph but conflicts with locked restore package references, so it was not kept. The standard compose build still lacks a clean result; Dockerfile was hardened with restore-layer lock files, `Directory.Build.props`, `.tmp/` ignore, first-time/audit suppression, and `--ignore-failed-sources`.
- 2026-08-27: Re-ran standard `docker compose -f docker-compose.yml -f docker-compose.dev.yml build assistant-service` with the hardened Dockerfile and clean context (`259KB`). It still stayed in `dotnet restore` for more than 10 minutes without a clean result, then was stopped manually. Re-tested Calendar list through gateway and Google still returns `accessNotConfigured`.
- 2026-08-28: Fixed the actual T053 root cause in code (T057): `GoogleWorkspaceMcpToolGateway` was mapping every Google 403 to `missing_scope`, including `accessNotConfigured` (Calendar API not enabled on the project), which telling the user to reconnect cannot fix. It now reads the provider's `error.errors[].reason` and only maps a genuine scope-shaped reason to `missing_scope`; everything else 403 becomes `provider_unavailable`. Backend/AI/web WT-565 changes committed across all three worktrees. Still pending: enabling Google Calendar API on the Google Cloud project (external step) and a live re-run of Calendar create through the gateway.
- 2026-08-28: Removed `--disable-parallel` from the AssistantService Dockerfile restore step and timed a clean `docker compose build assistant-service` end to end: 24.5 minutes, then failed. Restore log shows `Grpc.Tools.2.60.0` truncating mid-download from `api.nuget.org` three times ("response ended prematurely"); the eventual retry left a partially-broken restore, and `dotnet publish --no-restore` failed with `NETSDK1064` (missing `Microsoft.CodeAnalysis.Analyzers`). This means the earlier "slow NuGet restore" diagnosis was right about direction but incomplete: it isn't just slow, larger packages are getting corrupted in transit, and removing `--disable-parallel` did not fix it because restore parallelism was never the actual variable. Not yet re-tested from an unsandboxed host/CI runner, so it's still open whether this is a Dockerfile defect or a network condition specific to the environment that ran this build.
- Pending: Enable/configure Google Calendar API for the Google Cloud project and re-run confirmed Calendar write; get a clean standard AssistantService Docker build from a host without the truncated-download symptom, or diagnose why larger NuGet packages truncate in this build environment.
