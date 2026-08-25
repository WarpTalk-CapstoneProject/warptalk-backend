# Implementation Plan: MCP App Plugins for WarpBot

**Branch**: `feat/wt-565-mcp-plugins` | **Created**: 2026-08-23 | **Last updated**: 2026-08-25
**Spec**: `specs/565-mcp-app-plugins/spec.md` | **Tasks**: `specs/565-mcp-app-plugins/tasks.md`
**Repos on this branch**: `warptalk-backend`, `warptalk-web`, `warptalk-ai` (worked in `_worktrees/wt-565-*`)

## Status

| Phase | State |
|---|---|
| 0-6 Implementation (T001-T032) | Done, pushed |
| 7 Automated verification (T033-T035, T033A) | Green after merging `origin/development` on 2026-08-25 |
| 7 Manual E2E (T036) | **Blocked** - needs a real Google OAuth client + running Postgres |
| 8 Pre-merge hardening (T037-T043) | T037, T040, T043 done; T038-T039, T041-T042 open - see "Known gaps" |

This file is now an *as-built* plan: sections 1-8 describe what actually exists on the branch, section 9 lists the deltas from the original design, and sections 10-12 are the remaining work.

## 1. Summary

Users install Google Workspace **for their own account**, connect **their own** Google account, and WarpBot executes MCP-backed tools through `AssistantService` when the active workspace allows personal plugins. No new microservice; no provider credentials outside `AssistantService`; no hardcoded plugin rows in the frontend; no Public/Personal tabs.

## 2. Technical Context

**Language/Version**: .NET 10 backend, TypeScript/Next.js frontend, Python AI worker
**Primary Dependencies**: ASP.NET Core, EF Core/Npgsql, ASP.NET Data Protection, SignalR, Redis Streams, React Query, existing `warptalk-ai` tool-calling loop
**Storage**: PostgreSQL `assistant` schema - 4 new tables, OAuth material stored encrypted
**Testing**: xUnit (44 plugin tests), pytest (16 MCP tests), web contract scripts + `tsc --noEmit`
**Constraints**: MVP stays inside `AssistantService`; installation/connection are **personal**, the workspace only gates *usage* via `AllowAnyPlugins`
**Scale/Scope**: one provider (`google_workspace`), **three** shipped tools (see section 9, G1)

## 3. Constitution Check

- [x] Clean Architecture - `Domain` has no Google/MCP/HTTP/EF references; `Application` depends on interfaces; `Infrastructure` owns OAuth, MCP gateway, protectors, gRPC client; controllers only map HTTP.
- [x] Communication - `warptalk-ai` calls `AssistantService` over HTTP with the **caller's** bearer token; it never sees provider tokens.
- [x] API Standards - all routes under `/api/v1/assistant/...`, structured `errorCode` on expected failures.
- [x] Security - OAuth tokens encrypted at rest, never serialized to frontend or AI worker; execution scoped by `userId` from the token and gated by workspace policy.
- [x] TDD - Phase 0 tests written before implementation.
- [x] Scope Control - interfaces (`IPluginOAuthClient`, `IMcpToolGateway`, `IWorkspacePluginPolicyClient`) are provider-agnostic so an IntegrationService extraction stays possible.

## 4. As-built inventory

### warptalk-backend

```text
assistant/database/migrations/
  20260823090000_add_mcp_plugin_tables.sql        4 tables + google_workspace seed row

assistant/src/WarpTalk.AssistantService.Domain/
  Constants/PluginConstants.cs                    plugin key, statuses, effects, 9 error codes
  Entities/{Plugin,PluginInstallation,PluginConnection,PluginToolAudit}.cs
  Interfaces/IPlugin*Repository.cs                4 repositories, added to IUnitOfWork

assistant/src/WarpTalk.AssistantService.Application/
  DTOs/PluginDtos.cs                              catalog, connection, tool descriptor, exec req/result
  Interfaces/                                     IPluginInstallationService, IPluginConnectionService,
                                                  IPluginTokenRefresher, IMcpToolGateway,
                                                  IMcpToolOrchestrator, IPluginOAuthClient,
                                                  IPluginCredentialProtector,
                                                  IPluginOAuthStateProtector, IWorkspacePluginPolicyClient
  Services/{PluginInstallationService,PluginConnectionService,McpToolOrchestrator}.cs
  Helpers/{McpConfirmationTokenFactory,McpToolAuditRecorder}.cs
  Mappers/                                        PluginDefinition, PluginCatalogItem, PluginScope,
                                                  McpToolAudit, McpToolExecutionResult

assistant/src/WarpTalk.AssistantService.Infrastructure/
  OAuth/{GoogleWorkspaceOAuthClient,GoogleWorkspaceOAuthOptions}.cs
  Mcp/{GoogleWorkspaceMcpToolGateway,GoogleWorkspaceApiOptions}.cs
  Security/DataProtectionPluginCredentialProtector.cs   purpose PluginCredentials.v1
  Security/DataProtectionPluginOAuthStateProtector.cs   purpose PluginOAuthState.v1
  Clients/WorkspacePluginPolicyGrpcClient.cs            reads AllowAnyPlugins over gRPC
  Repositories/Plugin*Repository.cs

assistant/src/WarpTalk.AssistantService.API/
  Controllers/{AssistantPluginsController,AssistantMcpToolsController}.cs
  Program.cs                                      DI, lines 57-68
  appsettings.json                                Plugins:GoogleWorkspace:{OAuth,Api}

shared/WarpTalk.Shared/Protos/workspace.proto     AllowAnyPlugins on the settings message
workspace/...                                     policy surfaced through WorkspaceGrpcService,
                                                  WorkspaceSettingsDto, WorkspaceMapper,
                                                  WorkspaceConfiguration
meeting/...                                       assistant step/confirmation parity in room chat
assistant/tests/WarpTalk.AssistantService.Tests/Plugins/   5 test classes, 44 tests
```

### warptalk-ai

```text
ai_assistant_worker/mcp_tools.py       confirmation parameter injection, argument splitting,
                                       error-code -> userAction normalization, question card builder
ai_assistant_worker/chat_worker.py     _load_dynamic_mcp_tools (line ~845),
                                       _build_mcp_tool_handler (line ~912),
                                       dynamic tools merged into the tool table (line ~603)
shared/config.py                       ChatAssistantSettings.assistant_service_url
                                       (env ASSISTANT_CHAT_ASSISTANT_SERVICE_URL, default :5108)
tests/{test_mcp_tools,test_chat_agent_loop}.py    16 tests
```

### warptalk-web

```text
src/app/(app)/[workspaceSlug]/settings/plugins/page.tsx   marketplace, installed row, detail drawer,
                                                          disconnect/remove actions, search empty state
src/app/(app)/[workspaceSlug]/settings/page.tsx           allowAnyPlugins workspace switch
src/hooks/use-assistant.ts                                useAssistantPlugins, useInstallAssistantPlugin,
                                                          usePluginConnectUrl, useDisconnectAssistantPlugin,
                                                          useDisableAssistantPlugin
src/services/assistant.service.ts                         listPlugins, installPlugin,
                                                          getPluginConnectUrl, disconnectPlugin,
                                                          disablePlugin
src/lib/api/endpoints.ts                                  API.assistant.plugins*
src/components/layout/{global-chatbot,assistant-question-card}.tsx   confirmation card
src/components/rooms/live/chat-panel.tsx                  same confirmation card in room chat
scripts/check-plugin-{marketplace-contract,confirmation-surfaces}.mjs
```

## 5. Data model (as migrated)

`20260823090000_add_mcp_plugin_tables.sql` creates, in schema `assistant`:

| Table | Key columns | Notes |
|---|---|---|
| `plugins` | `plugin_key` UNIQUE, `required_scopes_json`, `tools_json`, `is_active` | catalog is **data**, not code; seeds `google_workspace` (id `7f8f66db-...f38b1`) with 3 tools |
| `plugin_installations` | `user_id`, `plugin_id`, `status`, `installed_at`, `disabled_at` | personal scope; status `not_installed` / `installed` / `disabled` |
| `plugin_connections` | `user_id`, `plugin_id`, `provider_account_id`, `provider_email`, `encrypted_access_token`, `encrypted_refresh_token`, `token_expires_at`, `scopes_json`, `status` | status `not_connected` / `connected` / `revoked` / `expired` |
| `plugin_tool_audits` | `workspace_id`, `user_id`, `conversation_id`, `assistant_message_id`, `plugin_key`, `tool_name`, `input_summary`, `result_status`, `provider_resource_ref` | written on success **and** on every rejected attempt |

Adding a provider or a tool = insert/patch a `plugins` row + a gateway branch. No frontend change required.

## 6. API contract (as implemented)

```text
GET    /api/v1/assistant/plugins                      catalog + this user's install/connection state
GET    /api/v1/assistant/plugins/installed            installed subset            (not consumed by web)
POST   /api/v1/assistant/plugins/{pluginKey}/install  idempotent; re-install re-enables a disabled row
DELETE /api/v1/assistant/plugins/{pluginKey}          disable installation        (not wired in web, G4)

GET    /api/v1/assistant/plugins/{pluginKey}/connection      current connection status
GET    /api/v1/assistant/plugins/{pluginKey}/connect-url     builds Google consent URL + protected state
GET    /api/v1/assistant/plugins/{pluginKey}/oauth/callback  code -> token exchange, stores encrypted
DELETE /api/v1/assistant/plugins/{pluginKey}/connection      sets status = revoked

GET    /api/v1/assistant/mcp/tools?workspaceId={guid}        tools visible to this user in this workspace
POST   /api/v1/assistant/mcp/tools/execute                   McpToolExecutionRequest -> McpToolExecutionResult
```

`McpToolExecutionRequest`: `workspaceId?`, `pluginKey`, `toolName`, `arguments?`, `conversationId?`, `assistantMessageId?`, `confirmationToken?`
`McpToolExecutionResult`: `isSuccess`, `errorCode?`, `message?`, `data?`, `providerResourceRef?`, `confirmationToken?`

### Error codes (`PluginConstants.ErrorCodes`)

| Code | Raised when | Client behaviour |
|---|---|---|
| `unknown_plugin` | no active `plugins` row for the key | internal error |
| `unknown_tool` | tool name not in the plugin's `tools_json`, or required arguments missing | internal error |
| `permission_denied` | workspace `AllowAnyPlugins = false`, or confirmation token mismatch | WarpBot explains the workspace policy |
| `plugin_not_installed` | no `installed` installation for this user | WarpBot points to Settings -> Plugins |
| `connection_required` | no `connected` connection, empty stored token, a provider 401 that survives one refresh, or a refresh the provider **rejected** - Google 400 `invalid_grant`, nothing stored to refresh with, or stored material that no longer decrypts (which also flips the row to `expired`) | WarpBot asks the user to connect |
| `missing_scope` | granted scopes miss a tool scope, or provider 403 | WarpBot asks the user to reconnect |
| `confirmation_required` | write tool called without a token; response carries a fresh `confirmationToken` | WarpBot renders the confirmation card |
| `provider_rate_limited` | provider 429, on a tool call **or on a token refresh** | retry later, no state change |
| `provider_unavailable` | provider 5xx, timeout, network failure, or any unclassified failure, on a tool call **or on a token refresh** | degrade gracefully; the connection is left `connected` so the next turn retries |

## 7. Authorization chain (`McpToolOrchestrator.ExecuteAsync`, in order)

1. Plugin exists and `is_active` -> else `unknown_plugin`.
2. Tool exists in that plugin's descriptor list -> else `unknown_tool`.
3. If `workspaceId` present: `IWorkspacePluginPolicyClient.AllowsPluginUsageAsync` -> else `permission_denied` (audited).
4. Installation for `userId` with status `installed` -> else `plugin_not_installed` (audited).
5. Connection for `userId` with status `connected` -> else `connection_required` (audited).
6. Every `tool.RequiredScopes` present in `connection.ScopesJson` -> else `missing_scope` (audited).
7. If `tool.Effect == write` and no `confirmationToken` -> `confirmation_required` + freshly minted token (audited).
8. If `tool.Effect == write` and token does not match -> `permission_denied` (audited).
9. If `connection.AccessTokenExpiresAt` is within 60s of now (or already past), `IPluginTokenRefresher.RefreshAccessTokenAsync`. A failed refresh is **not** one thing (T043):
   - the provider **rejected the grant** (`invalid_grant`), nothing is stored to refresh with, or the stored material no longer decrypts -> the row becomes `expired` and the call returns `connection_required` (audited);
   - the provider or the network **got in the way** (5xx, 429, timeout, DNS, any non-`invalid_grant` 4xx) -> `plugin_connections.status` is left untouched and the call returns `provider_unavailable` / `provider_rate_limited` (audited under that code), so the next turn simply tries again.
10. `IMcpToolGateway.ExecuteAsync`. If it comes back `connection_required` **and** step 9 did not already refresh, refresh once and retry the call once. If that refresh is rejected, the row becomes `expired` and the call returns `connection_required`. If it fails transiently, the transient code **replaces** the gateway's `connection_required`: the 401 proves the access token is stale, the failed refresh proves nothing about the grant, and sending the user through a browser re-consent over a ten-second outage is the one mistake here that is expensive to undo. If the grant really is dead, the next turn's refresh gets `invalid_grant` and the user is told to reconnect then.
11. Result audited with `success` or the provider error code.

The refresh decision sits in `McpToolOrchestrator` (Application), after every gate so a call that
ends at confirmation does not burn the one refresh attempt an execution gets. The persistence -
re-encrypting through `IPluginCredentialProtector`, writing `expired` - sits in
`PluginConnectionService`, which implements the narrow `IPluginTokenRefresher` alongside
`IPluginConnectionService`. `Domain` is untouched: no Google, HTTP, or EF reference was added.

The permanent/transient split crosses the layer boundary **semantically**, not as HTTP:
`IPluginOAuthClient.RefreshAccessTokenAsync` returns a `PluginOAuthRefreshResultDto` carrying a
`PluginOAuthRefreshOutcome` (`Succeeded` / `GrantRejected` / `ProviderUnavailable` /
`ProviderRateLimited`). `GoogleWorkspaceOAuthClient` is the only place that reads a status code or
parses `{"error": ...}`; `Application` switches on the enum and never sees `HttpStatusCode`.
Only `GrantRejected` ends a connection - anything ambiguous, including an exception the client did
not foresee, degrades to transient, because expiring a row is effectively one-way (gate 5 rejects a
non-`connected` row before the refresh code runs) while a retry costs nothing.

`ListAvailableToolsAsync` applies gate 3 only, then returns the tools of every `installed` plugin - so a workspace with the policy off exposes **zero** plugin tools to the model rather than failing later.

## 8. Flows

### 8.1 Connect (OAuth)

1. Web calls `GET .../{pluginKey}/connect-url`.
2. `PluginConnectionService` asks `DataProtectionPluginOAuthStateProtector` to protect `{UserId, PluginKey}` and `GoogleWorkspaceOAuthClient.BuildAuthorizationUrl` to assemble the consent URL from `Plugins:GoogleWorkspace:OAuth` + the plugin's `required_scopes_json`.
3. User consents in Google; Google redirects to `.../{pluginKey}/oauth/callback?code=...&state=...`.
4. State is unprotected back to `{UserId, PluginKey}` - the callback trusts the protected state, not the session.
5. `ExchangeCodeAsync` returns access/refresh tokens, provider account id, email, granted scopes.
6. Tokens are `Protect`ed (purpose `PluginCredentials.v1`) and stored; status becomes `connected`.

### 8.2 Execute (read)

`chat_worker._load_dynamic_mcp_tools` -> `GET /mcp/tools?workspaceId=` with the caller bearer -> descriptors become `ChatTool`s merged into `TOOLS_BY_NAME` -> model calls one -> `_build_mcp_tool_handler` POSTs to `/mcp/tools/execute` -> orchestrator gates -> gateway calls Google -> JSON data returned to the model.

### 8.3 Execute (write, confirmed)

1. Write descriptors get an extra `confirmationToken` property injected by `with_mcp_confirmation_parameter`.
2. First call has no token -> `confirmation_required` + token.
3. `normalize_mcp_tool_payload` adds `userAction.type = confirm_write`; `build_mcp_confirmation_questions` renders a two-option card (Confirm / Cancel).
4. Web shows the card via `assistant-question-card.tsx` in both `/ai-chat` and the room chat panel.
5. On Confirm the model repeats the call with the token; gate 8 compares it in fixed time; the gateway executes; `providerResourceRef` is audited.

## 9. Known gaps (delta from the original design)

| # | Gap | Impact | Where |
|---|---|---|---|
| G1 | `google_drive_get_file` was in the MVP tool list but is **not** seeded and **not** implemented - three tools ship, not four | plan/reality mismatch; Drive results are search-only | migration seed, `GoogleWorkspaceMcpToolGateway` switch |
| G2 | ~~**No refresh-token flow.**~~ **Fixed 2026-08-25 (T037).** `IPluginOAuthClient` had no refresh method, so `encrypted_refresh_token` was written on consent and never read again - roughly 60 min later every tool call returned `connection_required` while the row still said `connected`, and the only recovery was disconnect + reconnect | resolved: `RefreshAccessTokenAsync` on the OAuth client + Google `grant_type=refresh_token` implementation; the orchestrator refreshes once per execution (expiry ahead of the call, or the provider's own 401) and retries the call once | `IPluginOAuthClient`, `GoogleWorkspaceOAuthClient`, `IPluginTokenRefresher`, `PluginConnectionService`, `McpToolOrchestrator` |
| G3 | ~~`ConnectionStatus.Expired` is **never written**~~ **Fixed 2026-08-25 (T037).** Only `Revoked` was ever written, on explicit disconnect, so the "Reconnect" state the UI already renders was unreachable for a real expiry | resolved: a refresh the provider rejects (or a connection with no stored refresh token) persists `status = expired`; `ListCatalogAsync` does not filter on status and `PluginCatalogItemMapper` passes it straight through, so the plugins page renders "Reconnect" | `PluginConnectionService.MarkExpiredAsync` |
| G4 | ~~No disconnect or remove action anywhere in the UI~~ **Fixed 2026-08-25.** `useDisconnectAssistantPlugin` was written but never called, and `DELETE /plugins/{pluginKey}` was unwired, so a connection could be created and never undone - which with G2 left no recovery path at all | resolved: the connect dialog now carries Disconnect and Remove behind an inline confirm, and Remove disconnects first so provider tokens do not linger | `assistant.service.ts`, `use-assistant.ts`, `endpoints.ts`, plugins page |
| G5 | Confirmation token is `base64(userId:workspaceId:pluginKey:toolName:arguments)` - deterministic, unsigned, no TTL, not single-use, and it is handed to the model inside the question card | the gate is a UX gate, not a security boundary: the model can re-derive or replay it without the user pressing Confirm | `McpConfirmationTokenFactory` |
| G6 | `GET /plugins/installed` and `API.assistant.installedPlugins` exist but nothing consumes them (the page derives the installed row from the catalog) | dead surface | backend controller, `endpoints.ts` |

G5 is now the one a reviewer will challenge; G2/G3, the demo-visible pair, closed with T037.

**Closed by T043 (2026-08-25), left open by T037 on purpose:** T037 funnelled *any* refresh
failure into `expired` - a Google 5xx, a timeout or a dropped connection ended the connection just
as firmly as a revoked grant. `GoogleWorkspaceOAuthClient` called `EnsureSuccessStatusCode()`, so
a 400 `invalid_grant` and a 503 arrived at `PluginConnectionService` as the same exception, and its
`catch (Exception)` sent both to `MarkExpiredAsync`. Because gate 5 rejects a non-`connected` row
before the refresh code is ever reached, that `expired` was sticky: one network blip cost the user
a full browser re-consent.

Now the OAuth client classifies the provider's answer into a `PluginOAuthRefreshOutcome` and only
`GrantRejected` (Google 400 `invalid_grant`), no stored refresh token, or undecryptable stored
material ends the connection. 5xx, 429, timeouts, network failures and any non-`invalid_grant` 4xx
leave `plugin_connections.status` alone and surface `provider_unavailable` /
`provider_rate_limited`, which the audit row records as well - so the next turn retries instead of
the user re-consenting. `invalid_client` is deliberately on the transient side: it means *our*
client id/secret is wrong, and reading it as a dead user grant would turn one bad config push into
a mass re-consent.

## 10. Remaining work

Tracked as T036-T042 in `tasks.md`.

**Blocking the demo**

- ~~**T037** - Refresh flow (G2/G3).~~ **Done 2026-08-25.** `IPluginOAuthClient.RefreshAccessTokenAsync` + a Google `grant_type=refresh_token` implementation; `PluginConnectionService` also implements the narrow `IPluginTokenRefresher` and owns the persistence (re-encrypt, keep the stored refresh token when Google omits a new one, write `expired` on failure); `McpToolOrchestrator` owns the decision - refresh before the call when the recorded expiry is within 60s, otherwise on the provider's 401, at most once per execution, then retry the call once. 13 new tests, 27 total.
- **T036 - Manual E2E.** `manual-e2e.md`, once section 11 is satisfied. T037 no longer blocks it.

**Pre-merge hardening**

- **T038 - Signed confirmation tokens (G5).** Replace the base64 payload with a Data Protection-protected, time-limited (<= 5 min), single-use token bound to `userId + pluginKey + toolName + argument hash`; persist or cache the nonce so a replay fails. Accept: replaying a used token returns `permission_denied`; a token minted for different arguments does not validate.
- **T039 - Ship or drop `google_drive_get_file` (G1).** Either add the gateway branch + seed patch migration, or delete it from spec/plan so the MVP tool list is three.
- ~~**T040** - Wire disconnect + disable (G4).~~ Done 2026-08-25.
- **T041 - Remove or use `plugins/installed` (G6).**
- **T042 - Ops readiness.** Confirm Data Protection keys are persisted and shared across AssistantService replicas - with the default in-memory/per-container key ring, every restart or second replica makes stored tokens and OAuth state undecryptable. Document `Plugins:GoogleWorkspace:OAuth:*` as deployment secrets.
- ~~**T043** - Tell a rejected grant apart from a transient refresh failure.~~ **Done 2026-08-25.** See section 9 and section 7 gates 9-10. 17 new/changed tests, 27 -> 44.

## 11. Local run / E2E prerequisites

1. Google Cloud -> OAuth 2.0 Client (Web application), authorized redirect URI `http://localhost:5108/api/v1/assistant/plugins/google_workspace/oauth/callback`, scopes `drive.readonly` + `calendar.events`.
2. Supply secrets **outside** git (`dotnet user-secrets` in `WarpTalk.AssistantService.API`, or env):
   - `Plugins:GoogleWorkspace:OAuth:ClientId`
   - `Plugins:GoogleWorkspace:OAuth:ClientSecret`
3. Postgres up, then apply `assistant/database/migrations/20260823090000_add_mcp_plugin_tables.sql`.
4. Run WorkspaceService (gRPC `:50056`, for `AllowAnyPlugins`), AssistantService (`:5108`), the `warptalk-ai` chat worker with `ASSISTANT_CHAT_ASSISTANT_SERVICE_URL=http://localhost:5108`, and `warptalk-web`.
5. Walk `manual-e2e.md`. The Google consent step must be performed by a human.

## 12. Merge & rollout

1. Land **backend** first - web and ai both call its endpoints. Migration is additive (`CREATE TABLE IF NOT EXISTS` + seed), no destructive step, safe to apply before deploy.
2. Then **ai** (tool discovery degrades to zero tools if the endpoint 404s - `_load_dynamic_mcp_tools` swallows failures), then **web**.
3. Secrets are per-environment; with `ClientId`/`ClientSecret` blank the connect-url call fails and the rest of WarpBot is unaffected.
4. Rollback: turn `AllowAnyPlugins` off at workspace level to disable the whole feature path without a deploy.

## Complexity Tracking

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|-------------------------------------|
| Confirmation token minted in `Application` rather than a dedicated security service | keeps the MVP inside AssistantService | a separate token service is unjustified for one provider - revisit with T038 |
