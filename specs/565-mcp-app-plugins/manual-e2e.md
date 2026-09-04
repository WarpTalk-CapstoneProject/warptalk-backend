# Manual E2E: Google Drive & Calendar MCP Plugin

Use this checklist for T036 after a real Google OAuth client is configured for local Gateway -> AssistantService routing.

## Required Local Configuration

- The infrastructure compose files hardcode `../warptalk-backend` / `../warptalk-web` as build
  contexts and ignore `BACKEND_SOURCE_ROOT` / `FRONTEND_SOURCE_ROOT`. To run the stack from the
  WT-565 worktrees, add the override in `local-e2e/compose.wt565.override.yml` as a third `-f`:
  it builds `frontend` from `../_worktrees/wt-565-web`, never docker-builds `assistant-service`
  (see T055), and turns the migrator into a no-op so migrations from whatever branch the main
  checkout is on are not applied. Apply assistant migrations by hand from
  `assistant/database/migrations/` (psql against `warptalk_assistant`, then record the file in
  `public.service_schema_migrations` with its sha256).
- AssistantService image: `dotnet publish -c Release -o <dir>/assistant-publish -p:UseAppHost=false`
  on the host, then `docker build -f local-e2e/Dockerfile.assistant-runtime -t
  warptalk-infrastructure-assistant-service:latest <dir>` (mirrors the Dockerfile `final` stage).
- AI worker: copy `warptalk-ai/.env` to `_worktrees/wt-565-ai/.env` (gitignored) and run
  `docker compose -p warptalk-ai -f docker-compose.yml up -d --build assistant` from the worktree.
- `POST /api/v1/assistant/plugins/catalog` requires the platform `admin` role (shared
  `SystemAdminAuthorization` policy), not the workspace `Admin` role. Locally, grant it with
  `INSERT INTO auth.user_roles(user_id, role_id)` for the operator test account.
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

## Linear (kind='mcp', WT-602 ladder) walkthrough

Adds a real third-party MCP server through the operator endpoint instead of a migration, then
proves the generic `kind='mcp'` path end to end. No plugin-specific code exists for Linear.

1. As an `admin`-role account, `POST /api/v1/assistant/plugins/catalog` with
   `{"pluginKey":"linear","label":"Linear","description":"...","mcpServerUrl":"https://mcp.linear.app/mcp"}`.
   Expect 201 and a row with `kind='mcp'`, `oauth_client_source='unresolved'`, `tools_json='[]'`.
2. Open `/settings/plugins`: the Linear card appears with Install, no code or restart.
3. Install, then Connect. The first connect-url call runs discovery and the ladder; expect
   `oauth_client_source='dcr'` and a populated `oauth_client_id` before the browser is redirected.
4. Authorize in Linear; the callback lands on the shared `/plugins/mcp/oauth/callback`. Expect
   `connected`, and `tools_json` populated from `tools/list` (`tools_synced_at` set).
5. In WarpBot, ask in plain language to create a Linear issue (no @mention). Expect a write
   confirmation card first, then a real issue after confirming.
6. With a second account that has Linear installed but not connected, repeat step 5. Expect the
   `plugin_connection_required` action card with a working Connect button.

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
- 2026-08-28: `docker compose build frontend` from the WT-565 web worktree succeeded cleanly in 2m14s, `/settings/plugins` present in the Next.js route manifest as a static route. Confirms the web-side WT-565 changes (personal plugins route, @mention tiles) build correctly in the compose context; the open Docker gap is isolated to AssistantService's NuGet restore.
- 2026-09-03: Applied `20260828100000_add_mcp_server_plugin_kind.sql` and `20260903120000_add_mcp_client_registration_source.sql` by hand to `warptalk_assistant` (the compose migrator mounts the main checkout, which was on `refactor/wt-603` and carried an unrelated pending meeting migration, so it was disabled via `local-e2e/compose.wt565.override.yml`). Recorded both in `public.service_schema_migrations`.
- 2026-09-03: `POST /api/v1/assistant/plugins/catalog` initially returned 403 for a token carrying `admin` under `ClaimTypes.Role`. Two fixes: the endpoint used `[Authorize(Roles = "Admin")]` (the workspace-admin role) and now uses the shared `SystemAdminAuthorization` policy (platform `admin`, registered via `AddWarpTalkSystemAdminAuthorization()`); and AssistantService `Program.cs` forced `RoleClaimType = "role"`, which never matched the auth service's `ClaimTypes.Role` claims, so no role check in this service could ever succeed. Assistant plugin/MCP tests `136/136` after the change.
- 2026-09-03: Rebuilt AssistantService runtime image from local Release publish (`local-e2e/Dockerfile.assistant-runtime`), frontend from the WT-565 web worktree, and the AI assistant worker from the WT-565 AI worktree; compose stack up with the override.
- 2026-09-03: Through gateway `:5200` as `mentor.demo@warptalk.vn` (granted local `admin` role): catalog insert returned 201; `GET /plugins` lists `linear` as `not_installed`; install returned `installed`/`not_connected`.
- 2026-09-03: `GET /plugins/linear/connect-url` ran the WT-602 ladder live against Linear: RFC 9728 protected-resource metadata -> RFC 8414 AS metadata (`client_id_metadata_document_supported=true`, `registration_endpoint` present, `token_endpoint_auth_methods_supported` = `client_secret_basic`/`client_secret_post`/`none`) -> rung 2 skipped because `Plugins:Mcp:Client:ClientMetadataUrl` is empty locally -> rung 3 `POST https://mcp.linear.app/register` returned 201. Row now reads `oauth_client_source='dcr'`, `oauth_cimd_supported=true`, `oauth_token_endpoint_auth_method='client_secret_post'`, client id stored, deprecation log line emitted as designed. The returned authorize URL targets `https://mcp.linear.app/authorize` with `resource=https://mcp.linear.app/mcp`, PKCE S256, and the shared redirect URI `http://localhost:5200/api/v1/assistant/plugins/mcp/oauth/callback`. Note: with a public CIMD host Linear would land on rung 2, not rung 3.
- 2026-09-03: Pending (needs a human Linear login): complete the consent screen, then verify `connected`, `tools_json` synced, plain-language issue creation with write confirmation, and the not-connected card for a second account (walkthrough steps 4-6 above).
- 2026-09-04: Catalog seeded through the operator endpoint with `local-e2e/seed-catalog.sh` (19 `kind='mcp'` rows; `catalog-seed.json` + `catalog-seed-google.json`). Ladder outcome per server, from real `connect-url` calls as `mentor.demo`: rung 3 DCR succeeded for linear, notion, asana, canva, zapier, monday, atlassian (`oauth_client_source='dcr'`, all negotiated `client_secret_post`; linear/notion/canva also advertise CIMD and will move to rung 2 once `ClientMetadataUrl` is public). GitHub, Slack, HubSpot: rung 4, `connect-url` answers 400 `No client registration mechanism applies...` because their authorization servers offer neither CIMD nor DCR - the row waits for an operator-registered OAuth app (rung 1). Figma: advertises DCR but `https://api.figma.com/v1/oauth/mcp/register` answers 403 to every registration regardless of redirect URI scheme; treated like rung 1.
- 2026-09-04: Atlassian (`https://mcp.atlassian.com/v1/mcp`) publishes no RFC 9728 protected-resource document (both well-known forms 404, `WWW-Authenticate` carries no `resource_metadata`), only RFC 8414 metadata at its origin - the MCP Authorization 2025-03-26 shape. Discovery used to refuse (`Could not read protected resource metadata`); `McpAuthorizationServerDiscovery` now falls back to the server origin as the issuer when no document exists, and a server that publishes nothing still fails with the same `provider_unavailable` code. New integration test `Discovery_FallsBackToTheServerOrigin_WhenThereIsNoProtectedResourceMetadata` (it first caught a null `scopes_supported` read on that path, fixed). Plugin/MCP tests `137/137`. After redeploy Atlassian connect-url returns its authorize URL and the row reads `dcr`.
- 2026-09-04: Google Workspace via Google's official remote MCP servers (Developer Preview; `gmailmcp`/`drivemcp`/`docsmcp`/`sheetsmcp`/`slidesmcp`/`calendarmcp`/`chatmcp`/`people` `.googleapis.com/mcp/v1`): Google publishes path-inserted RFC 9728 metadata and `accounts.google.com` OIDC discovery, but neither DCR nor CIMD, so the 8 rows are seeded as rung 1 with the project's existing Google OAuth client (placeholders expanded from `GOOGLE_WORKSPACE_CLIENT_ID/SECRET`, never written to disk). `connect-url` for gmail and google_drive returns an `accounts.google.com/o/oauth2/v2/auth` URL with the per-app scopes and `resource=<server url>`; rows read `preregistered`. Consent is not yet attempted: the operator must first enrol the Cloud project in the Workspace Developer Preview Program, enable each `*MCP` service, add the scopes to the consent screen, and add `<gateway>/api/v1/assistant/plugins/mcp/oauth/callback` as an authorised redirect URI on that client. Open question: the native `google_workspace` (Drive + Calendar) row now overlaps with `google_drive` / `google_calendar`; decide whether to deactivate the native row once the MCP rows are proven.
- 2026-09-04: DataProtection key ring was not persisted for AssistantService in the compose stack (`DataProtection:KeyRingPath` empty, no volume): every recreate of the container generated a fresh ring, so the DCR client secrets registered earlier that day (linear, notion, asana, canva, zapier, monday) and the first batch of Google client secrets became undecryptable. The named volume `assistant-data-protection-keys` still existed from an earlier stack and held a 2026-08-25 key. Fix: `local-e2e/compose.wt565.override.yml` now sets `DataProtection__KeyRingPath=/var/lib/warptalk/keys` on that volume (the Dockerfile already creates the directory); the live key was copied into the volume, the six DCR rows were reset to `unresolved` and re-registered (all `dcr` again), and the eight Google rows were deleted and re-seeded. **Prod check needed:** `deploy/production/app.compose.yml` and the k3s values carry no key-ring persistence for assistant-service either, which would invalidate every stored plugin secret and user token on each deploy.
- 2026-09-04: Operator confirmed the Google Cloud Console setup (Developer Preview, MCP services, scopes, shared MCP redirect URI). The generic `McpOAuthClient` authorize URL carried no `access_type=offline`, so Google would have issued no refresh token and every Google MCP connection would have expired after one hour (the same trap the native client hit on 2026-08-26). `BuildAuthorizationUrl` now always sends `access_type=offline`; other servers ignore it per RFC 6749 section 3.1. Tests `137/137`. Gmail consent handed to the operator in the browser; pending: `connected` + `tools_json` sync for gmail, then the remaining Google rows.
- Pending: Enable/configure Google Calendar API for the Google Cloud project and re-run confirmed Calendar write; get a clean standard AssistantService Docker build from a host without the truncated-download symptom, or diagnose why larger NuGet packages truncate in this build environment.
