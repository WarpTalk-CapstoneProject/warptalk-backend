# Tasks: MCP App Plugins for WarpBot

**Input**: `specs/565-mcp-app-plugins/spec.md`, `specs/565-mcp-app-plugins/plan.md`  
**Branch**: `feat/wt-565-mcp-plugins`

## Phase 0: Tests First

- [x] T001 [P] [US1] Add backend personal-scope tests for plugin install/disable in `assistant/tests/WarpTalk.AssistantService.Tests/Plugins/PluginInstallationServiceTests.cs`.
- [x] T002 [P] [US2] Add backend connection lifecycle tests in `assistant/tests/WarpTalk.AssistantService.Tests/Plugins/PluginConnectionServiceTests.cs`.
- [x] T003 [P] [US3] Add backend MCP execution orchestration tests, including workspace `AllowAnyPlugins`, in `assistant/tests/WarpTalk.AssistantService.Tests/Plugins/McpToolOrchestratorTests.cs`.
- [x] T004 [P] [US3] Add AI worker tests for dynamic MCP tool loading and execution proxying in `warptalk-ai/tests/test_chat_agent_loop.py`.
- [x] T005 [P] [US4] Add frontend test/check that plugin UI renders from API data and has no Public/Personal tabs in `warptalk-web/scripts/check-plugin-marketplace-contract.mjs`.

## Phase 1: Backend Domain

- [x] T006 [US1] Add provider-agnostic domain entities under `assistant/src/WarpTalk.AssistantService.Domain/Entities`: `PluginInstallation`, `PluginConnection`, `PluginToolAudit`.
- [x] T007 [US1] Add domain constants/value objects for plugin keys, statuses, connection statuses, tool effect type, and plugin error codes.
- [x] T008 [US1] Extend Assistant unit of work/repository abstractions for plugin entities without leaking EF into Domain.

## Phase 2: Backend Application

- [x] T009 [US1] Add DTOs for plugin catalog, installation status, connection status, MCP tool descriptors, and MCP execution result.
- [x] T010 [US1] Add interfaces `IPluginInstallationService`, `IPluginConnectionService`, `IMcpToolGateway`, `IMcpToolOrchestrator`, and workspace plugin policy client.
- [x] T011 [US1] Implement `PluginInstallationService` with personal account scope and idempotent install/disable behavior.
- [x] T012 [US2] Implement `PluginConnectionService` for connect URL creation, OAuth callback handling, disconnect, and current-user status.
- [x] T013 [US3] Implement `McpToolOrchestrator` to validate workspace policy, personal install state, user connection, scopes, confirmation, audit, and gateway execution.
- [x] T014 [US3] Add Google Workspace plugin definition to the backend catalog as configuration/registry data, not frontend hardcode.

## Phase 3: Backend Infrastructure

- [x] T015 [US1] Add EF mappings and migration files for `assistant.plugins`, `assistant.plugin_installations`, `assistant.plugin_connections`, and `assistant.plugin_tool_audits`.
- [x] T016 [US2] Implement credential encryption abstraction in Infrastructure and persist only encrypted OAuth tokens.
- [x] T017 [US2] Implement `GoogleWorkspaceOAuthClient` using environment-driven OAuth config.
- [x] T018 [US3] Implement MCP gateway/adapter abstraction for Google Workspace tools.
- [x] T018A [US3] Add WorkspaceService gRPC policy client so AssistantService can enforce `AllowAnyPlugins`.
- [x] T019 [US3] Add audit repository implementation and ensure failed execution attempts are recorded.

## Phase 4: Backend API

- [x] T020 [US1] Add `PluginsController` endpoints under `/api/v1/assistant/plugins`.
- [x] T021 [US3] Add `McpToolsController` endpoints under `/api/v1/assistant/mcp/tools`.
- [x] T022 [US1] Register plugin services, repositories, OAuth client, credential protector, and MCP adapter in AssistantService DI.
- [x] T023 [US1] Update Swagger/OpenAPI metadata for plugin and MCP endpoints.

## Phase 5: AI Worker

- [x] T024 [US3] Add dynamic MCP tool descriptor loading from AssistantService for the active workspace/user.
- [x] T025 [US3] Add MCP-backed ChatTool handlers that call AssistantService execution endpoint with caller bearer token.
- [x] T026 [US3] Map structured plugin errors to user-facing WarpBot responses, especially `connection_required` and `confirmation_required`.

## Phase 6: Frontend

- [x] T027 [US4] Add assistant plugin service methods and React Query hooks for catalog, install, connect URL, disconnect, and status.
- [x] T028 [US4] Add Personal Settings -> Plugins route/page using dynamic API data.
- [x] T029 [US4] Build plugin marketplace layout inspired by the reference: header, search, installed icon row, unified catalog list, and detail drawer.
- [x] T030 [US4] Ensure the plugin UI has no Public/Personal tabs and no hardcoded plugin rows.
- [x] T030A [US4] Add workspace settings `AllowAnyPlugins` switch for owner/admin policy.
- [x] T031 [US4] Update WarpBot Skills/Plugins menu to show installed plugin status and connect CTA.
- [x] T032 [US3] Add confirmation card UI for write MCP actions before execution.

## Phase 7: Verification

- [x] T033 Run targeted AssistantService tests for plugin services and MCP orchestration.
- [x] T034 Run AI worker tests for MCP plugin tools.
- [x] T035 Run frontend plugin marketplace typecheck and backend/web compile checks.
- [x] T033A Re-run T033-T035 after merging `origin/development` into the branch on 2026-08-25: AssistantService plugin tests 14/14, MeetingService assistant-step parity 11/11, AI worker MCP tests 16/16, web typecheck clean, plugin marketplace + confirmation contracts pass.
- [ ] T036 Manual E2E: user installs Google Drive & Calendar, connects Google through gateway redirect `http://localhost:5200/api/v1/assistant/plugins/google_workspace/oauth/callback`, WarpBot searches Drive, WarpBot reads a Drive file, and WarpBot creates Calendar event only after confirmation. Checklist: `specs/565-mcp-app-plugins/manual-e2e.md`.


## Phase 8: Pre-merge Hardening

Derived from the as-built gap review in `plan.md` section 9. T037 blocked a credible demo and is now done; T043 closed the limitation T037 left behind; the rest block merge review.

- [x] T037 [US2] Add a refresh-token flow (gap G2/G3). Done 2026-08-25. Before this, `encrypted_refresh_token` was written at consent and never read by anything, so the access token simply died about an hour in: every plugin tool call returned `connection_required` while `plugin_connections.status` still read `connected` and the Plugins page still said "Connected", and the only way out was disconnect + reconnect. `ConnectionStatus.Expired` existed in the constants and was never written.
  - Added `RefreshAccessTokenAsync` to `IPluginOAuthClient` and implemented it in `GoogleWorkspaceOAuthClient` against the Google token endpoint with `grant_type=refresh_token`; it returns `RefreshToken = null` when Google omits one (which is the normal case) and skips the user-info round trip.
  - `PluginConnectionService` now also implements a narrow `IPluginTokenRefresher`: it unprotects the stored refresh token, calls the provider, re-encrypts the new access token through `IPluginCredentialProtector`, keeps the stored refresh token when the response omits one, and saves through the unit of work. Any failure - no stored refresh token, undecryptable material, provider rejection - persists `status = expired` and returns `connection_required`.
  - `McpToolOrchestrator` owns the decision, deliberately placed after every authorization gate so a write tool still waiting on confirmation does not burn the attempt: refresh when `access_token_expires_at` is within 60s of now, otherwise when the gateway itself answers `connection_required` (the provider 401), at most once per execution, then retry the gateway call once.
  - The gateway stayed persistence-free and Domain gained no Google/HTTP/EF reference. DI resolves one scoped `PluginConnectionService` behind both interfaces.
  - Verified the "Reconnect" path lines up end to end: `ListCatalogAsync` does not filter connections by status and `PluginCatalogItemMapper` passes `connection.Status` through unchanged, and the plugins page maps `expired` to "Reconnect". No web change needed. A successful reconnect through the existing OAuth callback already rewrites `Status = connected`; there is now a test pinning that.
  - Tests: 13 added (`GoogleWorkspaceOAuthClientTests` x3, `McpToolOrchestratorTests` x5, `PluginConnectionServiceTests` x5), suite 14 -> 27, all green.
  - Known limitation, recorded in `plan.md` section 9 and left open on purpose: a *transient* refresh failure also marks the connection `expired`, and gate 5 makes that sticky until the user reconnects. Separating `invalid_grant` from a Google 5xx needs the OAuth client to surface the status code and is out of T037's scope. **Closed by T043.**
- [x] T038 [US3] Replace the confirmation token (gap G5). Implemented Data Protection protection, five-minute TTL, canonical argument hashing, persisted claim row, atomic consume, and direct service/orchestrator tests for replay, expiry, changed arguments, and happy path.
  - Accept: replaying a consumed token returns `permission_denied`; a token minted for different arguments does not validate; an expired token returns `confirmation_required` again; concurrent consume succeeds once.
  - Implementation notes: canonicalize JSON arguments before hashing; store token id/nonce hash, user id, optional workspace id, plugin key, tool name, argument hash, expiry, consumed timestamp; validate then atomically consume before provider execution.
- [x] T039 [US3] Implement `google_drive_get_file` (gap G1) and rename the provider display label to `Google Drive & Calendar`. Implemented privacy-bounded metadata/content reads, refusal tests, and seed-patch migration.
  - Add a seed-patch migration that updates the `plugins` row label/description and inserts `google_drive_get_file` into `tools_json` when missing.
  - Implement the `GoogleWorkspaceMcpToolGateway` branch with privacy-bounded reads: sanitized metadata plus text content only for supported text/exportable files under a strict size limit; return explicit unsupported/too-large messages for binary or oversized files.
  - Add backend gateway tests and AI worker dynamic-tool routing tests for the fourth tool.
- [x] T040 [US4] Wire plugin disconnect + disable (gap G4). Done 2026-08-25: the gap was wider than first recorded - `useDisconnectAssistantPlugin` existed but no component called it, and `DELETE /api/v1/assistant/plugins/{pluginKey}` had no service method, so an installed and connected plugin could not be undone from the UI at all. Added `disablePlugin` to the service/endpoints/hooks, put Disconnect and Remove in the connect dialog behind an inline confirm, made Remove disconnect first so stored provider tokens do not outlive the plugin, and added a search empty state. Contract script now asserts both actions and the empty state.
- [x] T041 [US1] Remove or consume `GET /api/v1/assistant/plugins/installed` and `API.assistant.installedPlugins` (gap G6). Removed the dead backend endpoint; no active caller exists.
- [x] T042 Ops readiness: configure a persisted/shared ASP.NET Data Protection key ring and document the per-environment Google OAuth secrets. Compose mounts `assistant-data-protection-keys`, sets `DataProtection__KeyRingPath`, and the Assistant image pre-creates the key directory owned by runtime UID `1654` so the first key can be persisted.
- [x] T043 [US2] Stop a transient refresh failure from killing a connection (the limitation T037 left open). Done 2026-08-25. **What was wrong:** every way a token refresh could fail looked identical by the time anything could act on it. `GoogleWorkspaceOAuthClient.RefreshAccessTokenAsync` called `response.EnsureSuccessStatusCode()`, so Google answering 400 `invalid_grant` (the grant really is dead - revoked, password changed, token pruned) and Google answering 503, or a request timing out, or DNS failing, all arrived as the same exception. `PluginConnectionService.RefreshAccessTokenAsync` caught `Exception` and sent every one of them to `MarkExpiredAsync`, which writes `plugin_connections.status = expired`. Gate 5 of `McpToolOrchestrator` rejects any non-`connected` row *before* the refresh code is reached, so that write was effectively one-way: a five-second Google hiccup permanently ended the connection and the only way back was a full OAuth re-consent in the browser. The user-visible cost of a network blip was the same as the cost of revoking access.
  - **Layer boundary.** `IPluginOAuthClient.RefreshAccessTokenAsync` now returns `PluginOAuthRefreshResultDto` (`Outcome` + optional `Token` + a diagnostic `Detail`) instead of a token-or-throw. `PluginOAuthRefreshOutcome` is `Succeeded` / `GrantRejected` / `ProviderUnavailable` / `ProviderRateLimited` - provider-neutral by design. `GoogleWorkspaceOAuthClient` is the only code that reads an `HttpStatusCode` or parses Google's `{"error": ...}` body; `Application` switches on the enum. `Domain` gained nothing.
  - Chose a result object over a typed exception pair: an hourly token refresh failing because a provider is having a bad minute is an ordinary outcome, not an exceptional one, and the rest of this service already models expected failure as `Result` + `ErrorCode`. It also keeps a genuine bug distinguishable - an unforeseen exception is still an exception rather than being silently classified.
  - **Permanent** (connection ends, `status = expired`, `connection_required`): Google 400 `invalid_grant`, no stored refresh token, stored material that will not decrypt. **Transient** (status untouched, `provider_unavailable` / `provider_rate_limited`): 5xx, 429, timeouts, `HttpRequestException`, any non-`invalid_grant` 4xx, a 200 with no access token, and any unclassified exception. `invalid_client` is on the transient side on purpose: it means our own client credentials are wrong, and treating it as a dead user grant would turn one bad config push into a mass re-consent.
  - **Reactive path.** When the gateway's own 401 produced `connection_required` and the follow-up refresh then failed transiently, the old code answered `connection_required`. It now answers the transient code instead. The 401 proves the *access token* is stale; the failed refresh proves nothing about the *grant*, and pushing the user into a browser consent to fix an outage is the expensive mistake. If the grant is genuinely dead, the next turn's refresh gets `invalid_grant` and the user is told to reconnect then - one extra turn, self-correcting. Reasoning is in a comment at the call site.
  - `McpToolOrchestrator` passes the refresher's `ErrorCode`/`Error` through on both triggers instead of hardcoding `connection_required`, so `McpToolAuditRecorder` writes the code that actually happened. `refreshAttempted` (one refresh per execution) and the two triggers (60s expiry skew, gateway `connection_required`) are unchanged.
  - Tests: 17 added or rewritten, suite 27 -> 44, all green. `GoogleWorkspaceOAuthClientTests` +6 classification cases (`invalid_grant` -> `GrantRejected`, 503/429/`invalid_client`/unreachable host/HTML body); `PluginConnectionServiceTests` +4 transient cases and the T037 rejection test renamed to `..._WhenProviderRejectsTheGrant` so it is explicit the rejection is `invalid_grant`-shaped, plus an undecryptable-material case; `McpToolOrchestratorTests` +8 cases that wire the real `PluginConnectionService` in as `IPluginTokenRefresher` and assert the row is still `connected` after a transient failure.

## Phase 9: Full-scope Closeout Plan

- [x] T044 Align local OAuth redirect documentation and config examples to gateway `:5200`; keep `:5108` documented only as direct-service debugging.
- [x] T045 Re-run targeted backend tests after T038/T039/T041/T042: AssistantService plugin tests `56/56`, Google Workspace gateway tests included, and MeetingService assistant-step parity `11/11`.
- [x] T046 Re-run AI worker tests for dynamic MCP tool loading, confirmation payload normalization, and provider-error mapping: `31 passed` (`test_mcp_tools.py`, `test_chat_agent_loop.py`, `test_chat_tools.py`).
- [x] T047 Re-run web checks: `npm run typecheck`, plugin marketplace contract, and confirmation-surface contract all pass. Re-checked 2026-08-26 after moving Plugins to the personal `/settings/plugins` route and adding a legacy workspace-route redirect: local `npm run typecheck`, Docker `next build`, and `git diff --check` pass.
- [ ] T048 Run manual E2E through gateway `:5200`, capture pass/fail evidence in `manual-e2e.md`, and mark T036 complete only if install, connect, Drive search, Drive read, Calendar confirmation, account isolation, and workspace policy gates pass.

## Phase 10: Continuation Gaps Before Merge

These tasks close the remaining gaps identified after the 2026-08-26 Docker/API smoke. They do not
change the core product decision: plugin installation and provider connection remain personal;
workspace settings only gate usage through `AllowAnyPlugins`.

- [x] T049 [US2] Extend backend MCP execution failures for `connection_required` with reconnect metadata. Done 2026-08-27: `McpToolExecutionResult` now carries plugin key/label, connection status, and connected account email for reconnectable failures; workspace policy blocks remain `permission_denied` without reconnect metadata; AssistantService plugin tests pass `66/66`.
  - Add fields to `McpToolExecutionResult`: `pluginKey`, `pluginLabel`, `connectionStatus`, optional `connectedAccountEmail`.
  - Populate metadata when no connection exists, connection is `expired`/`revoked`, or refresh permanently expires the row.
  - Keep `permission_denied` for `AllowAnyPlugins=false` without reconnect metadata.
  - Add tests for `not_connected`, `expired`, `revoked`, and policy-blocked workspace behavior.
- [x] T050 [US3] Map backend reconnect metadata in the AI worker. Done 2026-08-27: `connection_required` with metadata maps to `plugin_connection_required`, legacy payloads still map to `connect_plugin`, and the worker publishes a `pluginConnection` action payload for clients; targeted pytest passes `52/52`.
  - Change `connection_required` normalization from generic `connect_plugin` to `plugin_connection_required` when metadata is present.
  - Preserve fallback behavior for old payloads that only include `errorCode=connection_required`.
  - Ensure the model does not fabricate a provider-backed answer when tool execution is blocked by connection state.
  - Add/extend `tests/test_mcp_tools.py` and chat-loop tests.
- [x] T051 [US3] Render plugin reconnect/connect action cards inside WarpBot chat. Done 2026-08-27: global chatbot and room chat parse `pluginConnection` payloads, render `PluginConnectionActionCard`, support local `Not now`, and open the existing connect-url flow for Connect/Reconnect.
  - Add a reusable `PluginConnectionActionCard` beside the existing assistant action-card components.
  - Wire it in global chatbot and room chat surfaces.
  - Primary action uses the existing connect-url hook and opens Google OAuth in a new tab.
  - Secondary `Not now` dismisses locally and does not mutate backend state.
  - The card must link/manage personal plugins through `/settings/plugins`, not workspace settings.
- [x] T052 [US4] Add frontend contracts for plugin connection action cards. Done 2026-08-27: added `check-plugin-connection-action-contract.mjs`, wired `test:plugin-connection-action`, and re-ran web typecheck plus plugin marketplace/confirmation/connection-action contracts.
  - Assert global chatbot can render `plugin_connection_required`.
  - Assert room chat can render the same action.
  - Assert primary action depends on the connect-url hook/service.
  - Assert no workspace-scoped plugins route dependency returns.
- [ ] T053 [US3] Complete provider manual E2E after a fresh Google reconnect. Partially verified 2026-08-27 through gateway on the rebuilt AssistantService runtime image: Drive search returns real provider files; `google_drive_get_file` returns bounded text content for `text/plain` and explicit unsupported metadata for Google Sheets/octet-stream files; Calendar create returns `confirmation_required` and no event is written before confirmation; expired connection execution returns reconnect metadata for the in-chat action card. Still open because Google Calendar list/create is currently rejected by Google reason `accessNotConfigured`, now surfaced as `provider_unavailable` **by T057's gateway fix** (2026-08-28, previously misclassified as `missing_scope`); the code-side fix is done, the remaining action is to enable Google Calendar API on the Google Cloud project and re-run confirmed Calendar write live.
  - Drive search from WarpBot.
  - Drive get/read file from WarpBot.
  - Calendar create shows confirmation and does not write before confirmation.
  - Confirmed Calendar create writes the event.
  - Expired/revoked/not-connected card appears in global and room chat.
- [x] T054 [US1] Re-verify account and workspace boundaries after reconnect-card changes. Done 2026-08-27 through gateway on the compose stack: account A sees `google_workspace` installed/connected to `hanhnhi10022005@gmail.com`; account B in the same workspace sees `not_installed`/`not_connected`; `AllowAnyPlugins=false` returns zero tools and tool execution returns `permission_denied` with reconnect metadata null; restoring `AllowAnyPlugins=true` returns all four tools.
  - Account B in the same workspace does not inherit account A's plugin installation or connection.
  - `AllowAnyPlugins=false` exposes zero plugin tools and does not show a reconnect card.
  - `AllowAnyPlugins=true` restores tools for the personally connected account.
- [ ] T055 [Ops] Rebuild and smoke runtime images after T049-T052.
  - Infra compose config is quiet.
  - Frontend image builds from WT-565 worktree; verified 2026-08-27 with Docker `next build` and `/settings/plugins` in the route manifest.
  - AssistantService local Release publish succeeds; a runtime smoke image rebuilt from that publish boots in compose and served the 2026-08-27 gateway E2E checks. AssistantService Dockerfile now copies `Directory.Build.props` and lock files before restore, ignores `.tmp/`, disables first-time workload/telemetry/audit noise, and uses BuildKit NuGet package/cache mounts plus `--disable-parallel --ignore-failed-sources` locked restore to make retrying Docker builds less fragile. A direct SDK container restore with the host NuGet cache mounted succeeds, proving the project graph is restorable in Linux. A minimal Docker restore harness shows the unseeded-container path is not deadlocked but spends minutes downloading packages from NuGet (`Microsoft.EntityFrameworkCore.Design` index alone took ~67s) and timed out after 180s while still progressing. A container-only EF design-time exclusion was rejected because it conflicts with locked restore package references, so it was not kept. Re-running standard compose build with clean context stayed in restore for more than 10 minutes without a clean result; this task cannot be checked yet.
  - AI assistant Docker target builds and boots against Redis; verified 2026-08-27 with temporary smoke container.
  - Remove any temporary smoke AI container after boot.
- [ ] T056 [Review] Split and clean PR-ready changes.
  - Keep unrelated infrastructure ERD/Qdrant/database-doc changes out of WT-565 commits.
  - Commit order: backend, AI, web, infra config/docs.
  - Run final `git diff --check` in every touched repo.

## Phase 11: Gaps closed while re-verifying before commit (2026-08-28)

Found by re-reading the working tree against the docs above, which had drifted: real, tested code
already existed for part of T053 and for a few other gaps that were never given a task number.
These three tasks name that code so "done" in this file matches what ships.

- [x] T057 [US3] Stop misclassifying `accessNotConfigured` as a scope problem (closes the T053
  root cause). `GoogleWorkspaceMcpToolGateway` mapped every Google 403 to `missing_scope`,
  telling the user to reconnect with more scopes — wrong when the real problem is that the Google
  Cloud project itself hasn't enabled the Calendar API (`accessNotConfigured`), which reconnecting
  cannot fix. The gateway now parses `error.errors[].reason` (falling back to `error.status`) and
  only maps a real insufficient-scope reason (`insufficientPermissions`,
  `ACCESS_TOKEN_SCOPE_INSUFFICIENT`) to `missing_scope`; every other 403 reason returns
  `provider_unavailable` with the provider's reason in the message. Tests added in
  `GoogleWorkspaceMcpToolGatewayTests.cs`. Live re-verification against a real Calendar call is
  T053's remaining item, tracked there.
- [x] T058 [US2] Extend reconnect metadata to the never-connected path; make the OAuth callback
  browser-safe; revoke on disconnect. `McpToolOrchestrator`'s reconnect metadata (T049) only
  populated on the refresh-failure branch — a user who never connected at all still got a bare
  `connection_required` with no `pluginKey`/`pluginLabel`, so the in-chat action card (T051)
  couldn't render for that case. `BuildConnectionRequiredResult` now runs for the `connection ==
  null` branch too. Separately, `AssistantPluginsController`'s OAuth callback returned raw JSON,
  but Google redirects the end user's own browser straight at that URL — no human should land on
  an API response; it now redirects (302) to `/settings/plugins` and lets the page's own refetch
  show the outcome. `IPluginOAuthClient.RevokeTokenAsync` + a `GoogleWorkspaceOAuthClient`
  implementation make disconnect best-effort revoke the token at Google and clear stored token
  material locally, so a removed plugin doesn't leave a live grant at the provider. Tests added in
  `PluginConnectionServiceTests.cs` and `McpToolOrchestratorTests.cs`.
- [x] T059 [US4] Tool `resourceKey` grouping + `@mention` for installed/connected plugins. A
  plugin's tools can now declare `resourceKey`/`resourceLabel`/`resourceAvatarUrl`
  (`McpToolDescriptorDto`, backed by migration `20260826130000_add_plugin_tool_resource_groups.sql`)
  so one OAuth connection renders as separate tiles per product (Drive vs Calendar) without the
  frontend hardcoding provider-specific logic — `src/lib/assistant/plugin-tiles.ts`
  (`toDisplayTiles`), shared between the Plugins settings page and the new `@mention` picker.
  `AssistantMentionDto.entityType` gained `"plugin"`; `global-chatbot.tsx` offers only
  installed-**and-connected** plugins as mentionable, and `chat_worker.py`'s `_format_mentions`
  tells the model to prefer that plugin's tools for the turn. Scope note: this shipped only in the
  global WarpBot widget — room chat (`chat-panel.tsx`) has no `@mention` picker at all (not a
  WT-565 regression; it never had one for members/rooms/documents either), so there is no room-chat
  parity gap to close here. New web contract: `scripts/check-plugin-mention-contract.mjs`
  (`npm run test:plugin-mention`).

## Blockers

- T036 no longer needs a new Google redirect URI: `http://localhost:5200/api/v1/assistant/plugins/google_workspace/oauth/callback` has been registered. Docker/Postgres is running and the plugin, confirmation-token, and Drive get-file migrations are applied locally. Google consent has completed successfully once through the gateway callback and the connection now has a stored refresh token. Workspace policy gRPC routing is fixed in compose and verified through gateway: `AllowAnyPlugins=true` exposes four tools, `false` exposes zero, then `true` restores four. Account isolation is verified through gateway with a second demo account in the same workspace. Drive search/read and pre-write Calendar confirmation are verified; remaining manual E2E proof is confirmed Calendar write after enabling/configuring Google Calendar API for the Google Cloud project (`accessNotConfigured`, gateway-side misclassification already fixed by T057 — this is now purely a Google Cloud Console step, not a code gap).
- T055's Docker build blocker is unresolved as of 2026-08-27: standard `docker compose build assistant-service` still doesn't finish `dotnet restore` cleanly. The diagnostic evidence rules out a restore deadlock (a direct SDK-container restore with the host NuGet cache mounted succeeds), so the remaining work is re-timing the build now that `--disable-parallel` is suspect as the actual cause of the slowness rather than its fix.

## Dependencies & Execution Order

- Phase 0 tests must be created before implementation.
- Backend Domain/Application/Infrastructure/API phases should be implemented before frontend depends on real endpoints.
- AI worker can implement dynamic tool loading after backend `GET /api/v1/assistant/mcp/tools` exists.
- Frontend can initially use API mocks only inside tests, but production UI must use backend data.
- ~~T037 should land before T036 is attempted, otherwise the E2E walkthrough dies at the one-hour mark.~~ T037 landed 2026-08-25, so T036 is no longer time-boxed to the access-token lifetime.
- T038 should land before final T036 so the write-confirmation E2E validates the security boundary reviewers will inspect.
- T039 should land before final T036 so the E2E validates full scope: Drive search, Drive read/get-file, Calendar list/create.
- T049-T052 should land before final T036/T048 so the manual walkthrough validates the in-chat recovery path rather than the old generic connect response.
- Merge order across repos is backend -> ai -> web (`plan.md` section 12).

## Notes

- Keep the MVP inside AssistantService; do not introduce a separate IntegrationService.
- Keep provider-specific OAuth/MCP details in Infrastructure.
- Avoid hardcoded provider rows in frontend components.
- Do not add Gmail in MVP.
