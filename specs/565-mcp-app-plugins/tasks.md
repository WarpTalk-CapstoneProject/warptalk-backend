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
- [ ] T036 Manual E2E: Owner installs Google Workspace, user connects Google, WarpBot searches Drive, WarpBot creates Calendar event only after confirmation. Checklist: `specs/565-mcp-app-plugins/manual-e2e.md`.


## Phase 8: Pre-merge Hardening

Derived from the as-built gap review in `plan.md` section 9. T037 blocked a credible demo and is now done; the rest block merge review.

- [x] T037 [US2] Add a refresh-token flow (gap G2/G3). Done 2026-08-25. Before this, `encrypted_refresh_token` was written at consent and never read by anything, so the access token simply died about an hour in: every plugin tool call returned `connection_required` while `plugin_connections.status` still read `connected` and the Plugins page still said "Connected", and the only way out was disconnect + reconnect. `ConnectionStatus.Expired` existed in the constants and was never written.
  - Added `RefreshAccessTokenAsync` to `IPluginOAuthClient` and implemented it in `GoogleWorkspaceOAuthClient` against the Google token endpoint with `grant_type=refresh_token`; it returns `RefreshToken = null` when Google omits one (which is the normal case) and skips the user-info round trip.
  - `PluginConnectionService` now also implements a narrow `IPluginTokenRefresher`: it unprotects the stored refresh token, calls the provider, re-encrypts the new access token through `IPluginCredentialProtector`, keeps the stored refresh token when the response omits one, and saves through the unit of work. Any failure - no stored refresh token, undecryptable material, provider rejection - persists `status = expired` and returns `connection_required`.
  - `McpToolOrchestrator` owns the decision, deliberately placed after every authorization gate so a write tool still waiting on confirmation does not burn the attempt: refresh when `access_token_expires_at` is within 60s of now, otherwise when the gateway itself answers `connection_required` (the provider 401), at most once per execution, then retry the gateway call once.
  - The gateway stayed persistence-free and Domain gained no Google/HTTP/EF reference. DI resolves one scoped `PluginConnectionService` behind both interfaces.
  - Verified the "Reconnect" path lines up end to end: `ListCatalogAsync` does not filter connections by status and `PluginCatalogItemMapper` passes `connection.Status` through unchanged, and the plugins page maps `expired` to "Reconnect". No web change needed. A successful reconnect through the existing OAuth callback already rewrites `Status = connected`; there is now a test pinning that.
  - Tests: 13 added (`GoogleWorkspaceOAuthClientTests` x3, `McpToolOrchestratorTests` x5, `PluginConnectionServiceTests` x5), suite 14 -> 27, all green.
  - Known limitation, recorded in `plan.md` section 9: a *transient* refresh failure also marks the connection `expired`, and gate 5 makes that sticky until the user reconnects. Separating `invalid_grant` from a Google 5xx needs the OAuth client to surface the status code and is out of T037's scope.
- [ ] T038 [US3] Replace the confirmation token (gap G5). Today `McpConfirmationTokenFactory` returns `base64(userId:workspaceId:pluginKey:toolName:arguments)` - deterministic, unsigned, unbounded in time, reusable, and visible to the model in the confirmation card. Mint a Data Protection-protected, single-use token with a TTL <= 5 minutes bound to `userId + pluginKey + toolName + argument hash`.
  - Accept: replaying a consumed token returns `permission_denied`; a token minted for different arguments does not validate; an expired token returns `confirmation_required` again.
- [ ] T039 [US3] Resolve `google_drive_get_file` (gap G1): either implement the gateway branch and add a seed-patch migration, or remove it from `spec.md`/`plan.md` so the MVP tool list is the three tools that actually ship.
- [x] T040 [US4] Wire plugin disconnect + disable (gap G4). Done 2026-08-25: the gap was wider than first recorded - `useDisconnectAssistantPlugin` existed but no component called it, and `DELETE /api/v1/assistant/plugins/{pluginKey}` had no service method, so an installed and connected plugin could not be undone from the UI at all. Added `disablePlugin` to the service/endpoints/hooks, put Disconnect and Remove in the connect dialog behind an inline confirm, made Remove disconnect first so stored provider tokens do not outlive the plugin, and added a search empty state. Contract script now asserts both actions and the empty state.
- [ ] T041 [US1] Remove or consume `GET /api/v1/assistant/plugins/installed` and `API.assistant.installedPlugins` (gap G6) - the page derives the installed row from the catalog, so both are currently dead.
- [ ] T042 Ops readiness: verify the ASP.NET Data Protection key ring is persisted and shared across AssistantService replicas. With the default per-container key ring, a restart or a second replica makes every stored OAuth token and in-flight OAuth state undecryptable. Document `Plugins:GoogleWorkspace:OAuth:ClientId` / `ClientSecret` as per-environment deployment secrets.

## Blockers

- T036 needs a real Google Cloud OAuth client. `Plugins:GoogleWorkspace:OAuth:ClientId` / `ClientSecret` are intentionally blank in `assistant/src/WarpTalk.AssistantService.API/appsettings.json`; supply them through user secrets or env, plus a running Postgres with `20260823090000_add_mcp_plugin_tables.sql` applied. Full prerequisites: `plan.md` section 11. The Google consent step must be performed by a human.

## Dependencies & Execution Order

- Phase 0 tests must be created before implementation.
- Backend Domain/Application/Infrastructure/API phases should be implemented before frontend depends on real endpoints.
- AI worker can implement dynamic tool loading after backend `GET /api/v1/assistant/mcp/tools` exists.
- Frontend can initially use API mocks only inside tests, but production UI must use backend data.
- ~~T037 should land before T036 is attempted, otherwise the E2E walkthrough dies at the one-hour mark.~~ T037 landed 2026-08-25, so T036 is no longer time-boxed to the access-token lifetime.
- Merge order across repos is backend -> ai -> web (`plan.md` section 12).

## Notes

- Keep the MVP inside AssistantService; do not introduce a separate IntegrationService.
- Keep provider-specific OAuth/MCP details in Infrastructure.
- Avoid hardcoded provider rows in frontend components.
- Do not add Gmail in MVP.
