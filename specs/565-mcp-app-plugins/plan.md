# Implementation Plan: MCP App Plugins for WarpBot

**Branch**: `feat/wt-565-mcp-plugins` | **Date**: 2026-08-23 | **Spec**: `specs/565-mcp-app-plugins/spec.md`  
**Input**: Feature specification from `specs/565-mcp-app-plugins/spec.md`

## Summary

Add a clean, MVP-sized MCP plugin layer inside `AssistantService` so users can install Google Workspace for their own account, connect their own Google account, and WarpBot can execute dynamic MCP-backed tools through the backend when the active workspace policy allows personal plugins. The UI will use the reference Plugins screen as visual inspiration, but plugin rows and installed icons must be API-driven and the MVP will not include Public/Personal tabs.

## Technical Context

**Language/Version**: .NET 10 backend, TypeScript/Next.js frontend, Python AI worker  
**Primary Dependencies**: ASP.NET Core, EF Core/Npgsql, SignalR, Redis Streams, React Query, existing `warptalk-ai` tool-calling loop  
**Storage**: PostgreSQL `assistant` schema for plugin installation, connection with encrypted credential fields, and audit tables  
**Testing**: xUnit backend tests, pytest AI worker tests, existing frontend lint/build and targeted component checks  
**Target Platform**: WarpTalk web app and backend services  
**Project Type**: Multi-repo web application feature spanning `warptalk-backend`, `warptalk-web`, and `warptalk-ai`  
**Performance Goals**: Plugin catalog/status should load within normal settings page latency; MCP read tools should fail gracefully when provider is slow; write tools must not execute without confirmation  
**Constraints**: No new microservice for MVP; no hardcoded frontend plugin catalog; no OAuth credentials outside AssistantService; no Public/Personal tabs in UI; plugin installation/connection state is personal, while workspace only gates usage through `AllowAnyPlugins`  
**Scale/Scope**: One MVP provider (`google_workspace`) with four initial tools

## Constitution Check

*GATE: Constitution in this checkout is a placeholder file under `.specify/memory/constitution.md`; project-specific rules are taken from existing WarpTalk architecture/spec conventions.*

- [x] Clean Architecture: Domain entities/value objects stay provider-agnostic; Application uses interfaces; Infrastructure owns OAuth/MCP/provider clients; API controllers only map HTTP.
- [x] Communication: `warptalk-ai` uses existing assistant tool-calling flow and calls AssistantService execution endpoint with caller bearer token; no direct provider token handling in AI worker.
- [x] API Standards: New HTTP routes use `/api/v1/assistant/...`; structured error codes are returned for expected plugin failures.
- [x] Security: OAuth credentials are encrypted at rest and never returned to frontend/AI worker; execution is scoped by user and gated by active workspace policy.
- [x] TDD: Write backend/AI/frontend tests before implementation tasks for each story.
- [x] Scope Control: Keep capability inside AssistantService for MVP; design interfaces so extraction to IntegrationService is possible later.

## Project Structure

### Documentation

```text
specs/565-mcp-app-plugins/
├── spec.md
├── plan.md
└── tasks.md
```

### Source Code

```text
assistant/
├── database/migrations/
├── src/WarpTalk.AssistantService.Domain/
├── src/WarpTalk.AssistantService.Application/
├── src/WarpTalk.AssistantService.Infrastructure/
└── src/WarpTalk.AssistantService.API/

warptalk-ai/
└── ai_assistant_worker/

warptalk-web/
└── src/
```

**Structure Decision**: Use the existing AssistantService four-layer structure for backend plugin/MCP orchestration. Frontend renders plugin marketplace/settings from service hooks. AI worker adds dynamic MCP-backed ChatTools that proxy execution through AssistantService.

## Data Model

Add assistant schema tables:

```text
assistant.plugins
- id
- plugin_key
- label
- description
- avatar_url
- provider
- required_scopes_json
- tools_json
- is_active

assistant.plugin_installations
- id
- user_id
- plugin_id
- status
- config_json
- installed_at
- disabled_at

assistant.plugin_connections
- id
- user_id
- plugin_id
- provider_account_id
- provider_email
- status
- encrypted_access_token
- encrypted_refresh_token
- token_expires_at
- scopes_json
- created_at
- updated_at

assistant.plugin_tool_audits
- id
- workspace_id
- user_id
- conversation_id
- assistant_message_id
- plugin_key
- tool_name
- input_summary
- result_status
- provider_resource_ref
- created_at
```

## Backend API Plan

```text
GET    /api/v1/assistant/plugins
GET    /api/v1/assistant/plugins/installed
POST   /api/v1/assistant/plugins/{pluginKey}/install
DELETE /api/v1/assistant/plugins/{pluginKey}

GET    /api/v1/assistant/plugins/{pluginKey}/connect-url
GET    /api/v1/assistant/plugins/{pluginKey}/oauth/callback
DELETE /api/v1/assistant/plugins/{pluginKey}/connection

GET    /api/v1/assistant/mcp/tools
POST   /api/v1/assistant/mcp/tools/execute
```

## MVP Provider

Plugin key: `google_workspace`.

Initial tools:

- `google_drive_search`
- `google_drive_get_file`
- `google_calendar_list_events`
- `google_calendar_create_event`

Defer broad Google Docs write scope. Gmail remains a separate privacy/scoping decision even if represented visually later.

## Frontend UI Plan

Create a Plugins screen inspired by the attached reference:

- Header: `Plugins`
- Subtitle: `Work with WarpBot across your favorite tools`
- Search input
- Installed plugin icon row
- Unified featured/catalog list
- Plugin detail drawer or panel

Explicit UI constraints:

- Do not hardcode plugin rows in the component.
- Do not implement Public/Personal tabs for MVP.
- Fetch catalog/status through services/hooks.
- Plugin states come from API: `Install`, `Connect`, `Connected`, `Manage`, `Reconnect`, `Disabled`.

## Complexity Tracking

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|-------------------------------------|
| None | N/A | N/A |
