# Implementation Plan: MCP App Plugins for WarpBot

**Branch**: `feat/wt-565-mcp-plugins` | **Created**: 2026-08-23 | **Last updated**: 2026-08-26
**Spec**: `specs/565-mcp-app-plugins/spec.md` | **Tasks**: `specs/565-mcp-app-plugins/tasks.md`
**Repos on this branch**: `warptalk-backend`, `warptalk-web`, `warptalk-ai` (worked in `_worktrees/wt-565-*`)

## Status

| Phase | State |
|---|---|
| 0-6 Implementation (T001-T032) | Done, pushed |
| 7 Automated verification (T033-T035, T033A) | Green after merging `origin/development` on 2026-08-25 |
| 7 Manual E2E (T036) | **In progress** - Google redirect URI is registered for gateway `:5200`; Docker/Postgres and migrations are ready; browser OAuth connect succeeded once; workspace policy discovery is fixed; provider-tool walkthrough needs a fresh Google reconnect with a stored refresh token |
| 8 Pre-merge hardening (T037-T043) | T037, T038, T039, T040, T041, T042, T043 done; only final cross-repo gates and manual E2E remain |
| 9 Full-scope closeout | Planned: signed confirmation tokens, Drive get-file/read, plugin label correction, dead API cleanup, ops readiness, manual E2E |
| 10 Continuation gaps (T049-T059) | T049-T052, T054, T057-T059 done. T053 code-fixed (T057), pending a live Calendar re-run after enabling Google Calendar API on the Google Cloud project. T055 (Docker build) and T056 (commit split) still open. |

This file is now an *as-built* plan: sections 1-8 describe what actually exists on the branch, section 9 lists the deltas from the original design, and sections 10-12 are the remaining work.

## 1. Summary

Users install Google Drive & Calendar **for their own account**, connect **their own** Google account, and WarpBot executes MCP-backed tools through `AssistantService` when the active workspace allows personal plugins. The internal plugin key stays `google_workspace` for compatibility. No new microservice; no provider credentials outside `AssistantService`; no hardcoded plugin rows in the frontend; no Public/Personal tabs.

## 2. Technical Context

**Language/Version**: .NET 10 backend, TypeScript/Next.js frontend, Python AI worker
**Primary Dependencies**: ASP.NET Core, EF Core/Npgsql, ASP.NET Data Protection, SignalR, Redis Streams, React Query, existing `warptalk-ai` tool-calling loop
**Storage**: PostgreSQL `assistant` schema - 4 new tables, OAuth material stored encrypted
**Testing**: xUnit (56 plugin tests), pytest (31 MCP/chat tests), web contract scripts + `tsc --noEmit`
**Constraints**: MVP stays inside `AssistantService`; installation/connection are **personal**, the workspace only gates *usage* via `AllowAnyPlugins`
**Scale/Scope**: one provider (`google_workspace`) with four shipped tools: Drive search, Drive get/read file, Calendar list, Calendar create.

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
src/app/(app)/settings/plugins/page.tsx                   personal plugin route
src/app/(app)/[workspaceSlug]/settings/plugins/page.tsx   legacy redirect to /settings/plugins
src/components/assistant/plugins/plugins-page.tsx         marketplace, installed row, detail drawer,
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
| `plugins` | `plugin_key` UNIQUE, `required_scopes_json`, `tools_json`, `is_active` | catalog is **data**, not code; seeds `google_workspace` (id `7f8f66db-...f38b1`) and the seed-patch migration brings the local row to the four-tool `Google Drive & Calendar` catalog |
| `plugin_installations` | `user_id`, `plugin_id`, `status`, `installed_at`, `disabled_at` | personal scope; status `not_installed` / `installed` / `disabled` |
| `plugin_connections` | `user_id`, `plugin_id`, `provider_account_id`, `provider_email`, `encrypted_access_token`, `encrypted_refresh_token`, `token_expires_at`, `scopes_json`, `status` | status `not_connected` / `connected` / `revoked` / `expired` |
| `plugin_tool_audits` | `workspace_id`, `user_id`, `conversation_id`, `assistant_message_id`, `plugin_key`, `tool_name`, `input_summary`, `result_status`, `provider_resource_ref` | written on success **and** on every rejected attempt |

Adding a provider or a tool = insert/patch a `plugins` row + a gateway branch. No frontend change required.

## 6. API contract (as implemented)

```text
GET    /api/v1/assistant/plugins                      catalog + this user's install/connection state
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
| G1 | `google_drive_get_file` was in the MVP tool list but was not seeded or implemented | resolved 2026-08-25: seed patch, gateway branch, privacy bounds, and refusal tests ship the fourth tool |
| G2 | ~~**No refresh-token flow.**~~ **Fixed 2026-08-25 (T037).** `IPluginOAuthClient` had no refresh method, so `encrypted_refresh_token` was written on consent and never read again - roughly 60 min later every tool call returned `connection_required` while the row still said `connected`, and the only recovery was disconnect + reconnect | resolved: `RefreshAccessTokenAsync` on the OAuth client + Google `grant_type=refresh_token` implementation; the orchestrator refreshes once per execution (expiry ahead of the call, or the provider's own 401) and retries the call once | `IPluginOAuthClient`, `GoogleWorkspaceOAuthClient`, `IPluginTokenRefresher`, `PluginConnectionService`, `McpToolOrchestrator` |
| G3 | ~~`ConnectionStatus.Expired` is **never written**~~ **Fixed 2026-08-25 (T037).** Only `Revoked` was ever written, on explicit disconnect, so the "Reconnect" state the UI already renders was unreachable for a real expiry | resolved: a refresh the provider rejects (or a connection with no stored refresh token) persists `status = expired`; `ListCatalogAsync` does not filter on status and `PluginCatalogItemMapper` passes it straight through, so the plugins page renders "Reconnect" | `PluginConnectionService.MarkExpiredAsync` |
| G4 | ~~No disconnect or remove action anywhere in the UI~~ **Fixed 2026-08-25.** `useDisconnectAssistantPlugin` was written but never called, and `DELETE /plugins/{pluginKey}` was unwired, so a connection could be created and never undone - which with G2 left no recovery path at all | resolved: the connect dialog now carries Disconnect and Remove behind an inline confirm, and Remove disconnects first so provider tokens do not linger | `assistant.service.ts`, `use-assistant.ts`, `endpoints.ts`, plugins page |
| G5 | Confirmation token was `base64(userId:workspaceId:pluginKey:toolName:arguments)` - deterministic, unsigned, no TTL, and reusable | resolved 2026-08-25: Data Protection protection, five-minute TTL, canonical argument hash, persisted claim row, and atomic consume | `McpConfirmationTokenService`, `plugin_confirmation_tokens` |
| G6 | `GET /plugins/installed` and `API.assistant.installedPlugins` existed but nothing consumed them (the page derives the installed row from the catalog) | dead surface | backend endpoint removed; no web caller exists |

G1, G5, and G6 are now closed; G2/G3, the demo-visible pair, closed with T037.

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

## 10. Continuation implementation plan

Tracked as T036-T048 in `tasks.md`. Land the remaining work in this order so every commit either removes a review objection or restores the promised scope.

### 10.1 Prep the E2E lane

- **T044 - Align local redirect/docs.** Local browser OAuth through compose/gateway uses `http://localhost:5200/api/v1/assistant/plugins/google_workspace/oauth/callback`. `5108` is the AssistantService direct port and should only be used for direct-service debugging. Update `manual-e2e.md`, this plan, and local run notes so Google Cloud, Gateway, and `Plugins:GoogleWorkspace:OAuth:RedirectUri` agree.
- **T036 - Manual E2E.** Run this last, after T038/T039, so the walkthrough exercises the review-ready path. Docker/Postgres and the WT-565 migrations are ready locally; browser OAuth has connected successfully once through the gateway callback; the remaining proof is the provider-tool walkthrough: Drive search, Drive get-file, Calendar confirmation/write, account isolation, and workspace policy gating.

### 10.2 Close the security gap reviewers will ask about

- **T038 - Signed, single-use confirmation tokens (G5).** Replace the base64 payload with a Data Protection-protected, time-limited token and a persisted nonce/claim row so replay fails across requests and replicas.
  - Add a small confirmation-token persistence model in the `assistant` schema, or an equivalent repository-backed store: token id/nonce hash, `user_id`, optional `workspace_id`, `plugin_key`, `tool_name`, `argument_hash`, `expires_at`, `consumed_at`, `created_at`.
  - Canonicalize tool arguments before hashing, so semantically identical JSON validates and changed arguments fail.
  - Protect a compact payload with `ITimeLimitedDataProtector` or the existing Data Protection abstraction: version, token id, user id, workspace id, plugin key, tool name, argument hash, expiry.
  - Validation order: unprotect/expiry -> user/plugin/tool/workspace match -> argument hash match -> atomically consume nonce -> execute.
  - Expected errors: replayed or wrong-user/wrong-tool/wrong-argument token returns `permission_denied`; expired token returns `confirmation_required` with a fresh token.
  - Tests: mint/validate happy path, replay rejection, wrong arguments, wrong user/workspace/tool, expired token, concurrent consume only succeeds once, audit status on rejection.

### 10.3 Restore the original provider scope

- **T039 - Implement `google_drive_get_file` and correct the provider label (G1).** **Implemented 2026-08-25.** Full scope ships Drive read/get-file, not a trimmed tool list.
  - Keep plugin key `google_workspace` for compatibility, but change display label/copy to **Google Drive & Calendar** so the UI does not imply Gmail or Docs-write support.
  - Add a seed-patch migration that updates the `plugins` row label/description and adds `google_drive_get_file` to `tools_json` if missing.
  - Implement the gateway branch in `GoogleWorkspaceMcpToolGateway`.
  - Read behavior should be privacy-bounded: return sanitized metadata plus text content only when the file type is supported and under a strict size limit; for Google Docs-style files, use Drive export to `text/plain`; for binary/oversized files, return metadata and an explicit unsupported/too-large message instead of dumping bytes to the model.
  - Add backend gateway tests for metadata success, exported text success, missing id validation, provider 404/403 mapping, and oversize/binary refusal.
  - Add AI worker/tool-schema tests proving the fourth tool is loaded and routed.
  - Update manual E2E to ask WarpBot to open/read a known Drive file after search.

### 10.4 Remove dead surface area

- ~~**T040** - Wire disconnect + disable (G4).~~ Done 2026-08-25.
- **T041 - Remove or deliberately consume `GET /plugins/installed` (G6).** **Implemented 2026-08-25:** removed the dead backend endpoint; repository-wide search found no web caller or frontend constant in the active worktrees.

### 10.5 Make it deployable

- **T042 - Ops readiness.** **Implemented 2026-08-25:** AssistantService uses a configurable Data Protection key-ring path, and local/production compose mounts the named `assistant-data-protection-keys` volume at that path. With the default per-container key ring, a restart or a second replica makes stored OAuth tokens, OAuth state, and T038 confirmation tokens undecryptable.
  - Add production/docker configuration for a shared key ring location or a documented managed key store. **Done:** `DataProtection__KeyRingPath` plus shared compose volume; multi-host deployments must replace the named volume with a managed shared store.
  - Document `Plugins:GoogleWorkspace:OAuth:ClientId`, `ClientSecret`, and `RedirectUri` as per-environment deployment secrets.
  - Confirm local compose uses gateway redirect URI `http://localhost:5200/api/v1/assistant/plugins/google_workspace/oauth/callback`.
  - Add a configuration/readiness check or runbook note so a missing key-ring persistence decision is visible before rollout.
- ~~**T043** - Tell a rejected grant apart from a transient refresh failure.~~ **Done 2026-08-25.** See section 9 and section 7 gates 9-10. 17 new/changed tests, 27 -> 44.

### 10.6 Final quality gate

- **T045 - Re-run targeted backend tests** **Done 2026-08-25:** AssistantService plugin tests `56/56`, Google Workspace gateway coverage included, MeetingService assistant-step parity `11/11`.
- **T046 - Re-run AI worker tests** **Done 2026-08-25:** dynamic MCP loading, confirmation normalization, provider-error mapping, and Drive get-file routing tests `31 passed`.
- **T047 - Re-run web checks** **Done 2026-08-25:** `npm run typecheck`, plugin marketplace contract, and confirmation-surface contract pass.
- **T048 - Manual E2E and evidence capture**: run `manual-e2e.md` through gateway `:5200`, record the exact config values used, and update T036 with pass/fail notes.

### 10.7 Continuation gaps to close before merge

The remaining work is no longer about the base plugin platform. It is about closing the
demo-visible recovery paths and making the three-repo change set reviewable. Implement these as
T049-T056 in `tasks.md`, in this order.

#### Gap A - In-chat reconnect action for expired personal plugins

**Decision:** implement in this WT-565 scope. The original spec already says that when WarpBot
needs a Google Drive or Calendar tool and no personal connection exists, WarpBot must show a
connect CTA instead of fabricating an answer. The current code still degrades to a generic
`connect_plugin` action and the frontend has no card to render inside the chat.

When WarpBot tries to use a personal plugin and the caller's provider connection is `expired`,
`revoked`, or `not_connected`, the recovery path must appear inside the WarpBot chat window. The
user should not have to discover Personal Settings -> Plugins after the model has already hit a
tool failure.

**Target UX**

- Render a compact action card inside the assistant response, matching the shape of the existing
  confirmation card and the ChatGPT plugin recovery pattern:
  - plugin icon + plugin label, e.g. `Google Drive & Calendar`
  - body copy based on connection state:
    - `expired`: "Your Google Drive & Calendar connection has expired. Reconnect it before WarpBot can use it for this request."
    - `revoked`: "Reconnect Google Drive & Calendar before WarpBot can use it for this request."
    - `not_connected`: "Connect Google Drive & Calendar before WarpBot can use it for this request."
  - secondary action: `Not now`
  - primary action: `Reconnect` or `Connect`
- `Not now` dismisses the card locally and does not mutate backend state.
- `Reconnect` calls the existing connect-url endpoint and opens the provider OAuth URL. The chat
  should show a small "Finish connecting in your browser" notice while the OAuth tab is open.

**Backend/API contract**

- Keep plugin install/connection personal. Workspace settings still only gate usage through
  `AllowAnyPlugins`.
- Extend MCP execution failure payloads so `connection_required` carries enough structured data
  for the AI worker and frontend to render the action card without guessing:
  - `pluginKey`
  - `pluginLabel`
  - `connectionStatus` (`not_connected`, `expired`, `revoked`)
  - optional `connectedAccountEmail`
  - optional `message`
- Preserve `connection_required` as the error code so existing clients keep degrading safely.
- Add the metadata directly to `McpToolExecutionResult` rather than hiding it in `data`, because
  `data` is provider output and is absent on expected failures.
- Populate the metadata in `McpToolOrchestrator` for:
  - no connection row -> `not_connected`
  - existing row with `expired` or `revoked`
  - refresh failure that permanently expires the row
- Do not attach reconnect metadata to `permission_denied`, especially workspace
  `AllowAnyPlugins=false`; policy-blocked usage is not fixed by Google OAuth.
- If the OAuth callback cannot produce a refreshable connection, do not report `connected`.
  Callback should return the persisted status (`expired`) and the chat card can explain that the
  provider did not issue long-lived access.

**AI worker contract**

- Map MCP `connection_required` into a `userAction` instead of plain prose:
  - `type: "plugin_connection_required"`
  - `pluginKey`
  - `pluginLabel`
  - `connectionStatus`
  - optional `connectedAccountEmail`
  - `message`
- Keep backward compatibility: if the backend returns an older `connection_required` payload with
  no plugin metadata, AI may fall back to the old generic connect message.
- Do not fabricate an answer from model knowledge when a requested provider-backed tool is blocked
  by connection state.
- Keep the action metadata similar to `confirm_write` so the same chat rendering pipeline can carry
  both "confirm a write" and "reconnect a plugin".

**Frontend implementation plan**

- Add a reusable `PluginConnectionActionCard` component near the existing assistant action-card
  surfaces.
- Wire the card in both WarpBot surfaces:
  - global chatbot
  - room chat
- On primary action:
  - call `usePluginConnectUrl({ pluginKey })`
  - open the returned URL with `window.open(url, "_blank", "noopener,noreferrer")`
  - refetch plugin catalog/status after the user returns or after a short polling window
- The card must not depend on a workspace-scoped plugins page. Personal plugin management remains
  `/settings/plugins`; the chat card is the contextual recovery path.
- Reuse the existing connect-url hook/service. Do not add a separate reconnect endpoint; reconnect
  is the same OAuth connect flow after a personal connection has become expired/revoked.

**Tests and contracts**

- Backend tests:
  - `connection_required` includes plugin label and connection status for `expired`
  - `connection_required` includes `not_connected` when no user connection exists
  - workspace `AllowAnyPlugins=false` still returns `permission_denied`, not reconnect UI metadata
- AI tests:
  - expired connection maps to `plugin_connection_required`
  - missing connection maps to `plugin_connection_required`
  - provider connection failures do not produce a fake natural-language answer
- Web contracts:
  - global chatbot renders the plugin connection card
  - room chat renders the same card
  - card primary action calls the connect-url hook
  - card has no workspace-settings route dependency

**Manual E2E**

1. Force `google_workspace` connection to `expired`.
2. Ask WarpBot to search Drive.
3. Verify the in-chat reconnect card appears.
4. Click `Not now`; verify no backend state changes.
5. Ask again and click `Reconnect`; verify the Google OAuth URL opens.
6. Complete OAuth.
7. Verify plugin status becomes `connected`.
8. Retry Drive search and Drive get-file.
9. Repeat the reconnect card check in room chat.

#### Gap B - Final manual E2E evidence

**Decision:** keep T036/T048 open until the remaining live evidence is captured after Gap A lands.
The 2026-08-27 gateway smoke on a rebuilt AssistantService runtime image proved tool discovery,
Drive search, Drive get-file bounded content, Drive get-file unsupported refusal, pre-write
Calendar confirmation, reconnect metadata, account isolation, and workspace policy gating. The
ticket is still not fully closed because confirmed Calendar write is currently blocked by Google
Calendar provider reason `accessNotConfigured`, and the final user-visible WarpBot/browser
walkthrough still needs to be captured.

Manual E2E must capture:

- plugin catalog loads from Personal `/settings/plugins`;
- Google OAuth callback returns to the app, not raw JSON;
- Drive search works from WarpBot;
- Drive get-file works or returns the bounded unsupported/too-large response; gateway evidence now
  covers both a `text/plain` content read and unsupported file refusals;
- Calendar create shows confirmation before provider write; gateway evidence confirms no event is
  written before confirmation;
- Calendar event exists only after confirmation;
- account B in the same workspace does not inherit account A's install/connection;
- `AllowAnyPlugins=false` hides plugin tools and does not render a reconnect card;
- expired/revoked/not-connected plugin renders the new in-chat action card in both global and room
  chat.

#### Gap C - Docker/runtime stability

**Decision:** make this a verification gate, not feature code. Docker is running again and the
compose stack is usable. Frontend and AI images have built, and AssistantService local Release
publish can be packaged into a runtime smoke image that boots in compose. The standard
AssistantService Docker build remains open because the compose build's container `dotnet restore`
step still lacks a clean result when the BuildKit cache is unseeded. A direct SDK container restore
with the host NuGet cache mounted succeeds, so the Linux project graph itself is valid. A verbose
minimal Docker restore harness showed slow NuGet downloads rather than an MSBuild deadlock, timing
out after 180s while still progressing. The Dockerfile now moves `Directory.Build.props` and lock
files into the restore layer, ignores `.tmp/`, suppresses first-time workload/telemetry/audit work,
and uses BuildKit package/cache mounts plus `--disable-parallel --ignore-failed-sources`. A
container-only EF design-time package exclusion would reduce the graph but conflicts with the
checked-in lock files, so it is not part of this plan.

Required runtime checks:

- `docker compose -f docker-compose.yml -f docker-compose.dev.yml config --quiet`
- `docker compose -f docker-compose.yml -f docker-compose.dev.yml build assistant-service frontend`
- `docker compose -f docker-compose.yml -f docker-compose.dev.yml up -d`
- `docker compose build assistant` in the AI worktree, or the equivalent CI image build
- smoke the AI assistant image with the existing ignored env file; remove the smoke container after
  boot to avoid duplicate Redis consumers
- verify `warptalk-postgres` is healthy and AssistantService sees WorkspaceService at
  `http://workspace-service:50056`

#### Gap D - Review hygiene and repo split

**Decision:** keep unrelated cleanup out of WT-565 commits. The infrastructure checkout currently
contains database/ERD/Qdrant changes that are not part of plugin work. Before PR/merge, split or
stash unrelated infra files and keep only WT-565 config/docs changes.

Commit grouping:

1. **Backend:** OAuth/revoke/refresh/confirmation/get-file/reconnect metadata, migrations, backend
   tests, WT-565 docs.
2. **AI:** plugin mention routing plus `plugin_connection_required` user action tests.
3. **Web:** personal Plugins route/sidebar/resource tiles plus in-chat reconnect cards/contracts.
4. **Infra:** compose/env examples required for WT-565 only, especially source roots, Data
   Protection key ring, gateway redirect, and WorkspaceService gRPC URL.

#### Gap E - Quality gates

Run these before marking T036/T048 complete:

- Backend:
  - `dotnet test assistant/tests/WarpTalk.AssistantService.Tests/WarpTalk.AssistantService.Tests.csproj --filter Plugins`
  - meeting assistant-step parity tests if confirmation payload code changes
  - `git diff --check`
- AI:
  - `python -m pytest tests/test_mcp_tools.py tests/test_chat_agent_loop.py tests/test_chat_templates.py -q`
  - build `assistant` Docker target
- Web:
  - `npm run typecheck`
  - `node scripts/check-plugin-marketplace-contract.mjs`
  - `node scripts/check-plugin-confirmation-surfaces.mjs`
  - add or extend a contract script for plugin connection action cards
  - Docker `next build` through infra compose
- Runtime:
  - gateway API smoke for plugin catalog/tools/execute
  - browser/manual E2E from `manual-e2e.md`

### 10.8 Gaps closed while re-verifying before commit (T057-T059)

Re-reading the working tree against this document on 2026-08-28 found real, already-written and
partly-tested code that this document had not caught up to. Named as tasks in `tasks.md` Phase
11; summarized here because they touch the authorization chain (section 7) and the data model
(section 5-6).

- **T057** closes the actual root cause under T053: `GoogleWorkspaceMcpToolGateway` mapped every
  Google 403 to `missing_scope`. `accessNotConfigured` (the Calendar API not enabled on the
  Google Cloud project) is not a scope problem and telling the user to reconnect cannot fix it.
  The gateway now reads `error.errors[].reason` and only routes a genuine
  `insufficientPermissions`/`ACCESS_TOKEN_SCOPE_INSUFFICIENT` reason to `missing_scope`; anything
  else 403 becomes `provider_unavailable` carrying the provider's reason.
- **T058** extends section 7 gate 5's reconnect metadata (added for the refresh-failure path by
  T049) to the `connection == null` case, so a user who never connected also gets a renderable
  in-chat action card instead of a bare `connection_required`. It also changes the OAuth callback
  endpoint (section 8.1 step 3) from returning JSON to a 302 redirect to `/settings/plugins` -
  Google puts the end user's own browser on that URL, so JSON was never a valid response for a
  human - and adds best-effort provider-side token revocation on disconnect
  (`IPluginOAuthClient.RevokeTokenAsync`).
- **T059** adds `resourceKey`/`resourceLabel`/`resourceAvatarUrl` to `McpToolDescriptorDto`
  (migration `20260826130000_add_plugin_tool_resource_groups.sql`) so one OAuth connection can
  render as multiple catalog tiles (Drive vs Calendar) - data-driven, no frontend
  provider-specific branching - and adds a `"plugin"` `AssistantMentionDto.entityType` so an
  installed-and-connected plugin is `@mention`-able in the global WarpBot widget. Deliberately
  scoped to the global widget only: room chat (`chat-panel.tsx`) has no `@mention` picker at all
  for any entity type, so there is no parity gap to close.

## 11. Local run / E2E prerequisites

1. Google Cloud -> OAuth 2.0 Client (Web application), authorized redirect URI `http://localhost:5200/api/v1/assistant/plugins/google_workspace/oauth/callback`, scopes `drive.readonly` + `calendar.events`.
   - This URI is already registered for local E2E through compose/gateway.
   - `http://localhost:5108/...` is only for direct AssistantService debugging and should not be used for the normal compose walkthrough.
2. Supply secrets **outside** git (`dotnet user-secrets` in `WarpTalk.AssistantService.API`, or env). For the local compose lane they are stored in ignored infrastructure `.env` values:
   - `Plugins:GoogleWorkspace:OAuth:ClientId`
   - `Plugins:GoogleWorkspace:OAuth:ClientSecret`
   - `Plugins:GoogleWorkspace:OAuth:RedirectUri=http://localhost:5200/api/v1/assistant/plugins/google_workspace/oauth/callback`
3. For compose-based local work, set `BACKEND_SOURCE_ROOT=../_worktrees/wt-565-backend` and `FRONTEND_SOURCE_ROOT=../_worktrees/wt-565-web` in the ignored infrastructure `.env` so the containers build the WT-565 worktrees.
4. Postgres up, then apply `assistant/database/migrations/20260823090000_add_mcp_plugin_tables.sql` and the later plugin seed/confirmation-token migrations.
5. Run WorkspaceService (gRPC `:50056`, for `AllowAnyPlugins`), AssistantService (`:5108` behind gateway `:5200`), the `warptalk-ai` chat worker with `ASSISTANT_CHAT_ASSISTANT_SERVICE_URL=http://assistant-service:8080` in compose or `http://localhost:5108` when running directly, and `warptalk-web`. In compose, AssistantService must receive `GrpcSettings__WorkspaceServiceUrl=http://workspace-service:50056`; `localhost:50056` only works when AssistantService itself is running directly on the host.
6. Walk `manual-e2e.md`. The Google consent step must be performed by a human.

## 12. Merge & rollout

1. Land **backend** first - web and ai both call its endpoints. Migration is additive (`CREATE TABLE IF NOT EXISTS` + seed), no destructive step, safe to apply before deploy.
2. Then **ai** (tool discovery degrades to zero tools if the endpoint 404s - `_load_dynamic_mcp_tools` swallows failures), then **web**.
3. Secrets are per-environment; with `ClientId`/`ClientSecret` blank the connect-url call fails and the rest of WarpBot is unaffected.
4. Rollback: turn `AllowAnyPlugins` off at workspace level to disable the whole feature path without a deploy.

## Complexity Tracking

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|-------------------------------------|
| Confirmation token minted in `Application` rather than a dedicated security service | keeps the MVP inside AssistantService | a separate token service is unjustified for one provider - revisit with T038 |
