# Feature Specification: MCP App Plugins for WarpBot

**Feature Branch**: `feat/wt-565-mcp-plugins`  
**Created**: 2026-08-23  
**Status**: Draft - updated after product scope change to personal plugins  
**Linear**: WT-565  
**Input**: User request: "Backend cài MCP của các app khác; không tạo module mới nếu làm phình scope; UI lấy cảm hứng từ Plugins screen, không hardcode, không chia tab Public/Personal."

## User Scenarios & Testing

### User Story 1 - Install MCP App For Account (Priority: P1)

As a signed-in user, I want to install an external app MCP integration for my own account so that WarpBot can expose the app's tools only for me.

**Why this priority**: Plugin availability is personal. WarpBot should know which external app tools the asking account installed without coupling installation to workspace membership.

**Independent Test**: Can be tested by signing in, installing Google Drive & Calendar, and verifying the plugin appears in the installed plugin list and dynamic WarpBot capabilities for that same account only.

**Acceptance Scenarios**:

1. **Given** a signed-in user, **When** they install Google Drive & Calendar from the plugins screen, **Then** the backend persists a personal plugin installation and returns the plugin as installed for that account.
2. **Given** another signed-in user in the same workspace, **When** they open the plugins screen, **Then** they do not inherit the first user's installation or provider connection.

---

### User Story 2 - Connect Personal Provider Account (Priority: P1)

As a user, I want to connect my own Google Drive and Calendar account after I install the plugin so that WarpBot can act only through my account and scopes.

**Why this priority**: MCP tool execution must use the asking user's own provider identity, not a shared admin token.

**Independent Test**: Can be tested by installing Google Drive & Calendar, connecting one user's Google account, and verifying the connection status is visible only for that user.

**Acceptance Scenarios**:

1. **Given** Google Drive & Calendar is installed by the account, **When** a user completes OAuth, **Then** the backend stores encrypted credential fields on the user plugin connection and reports the provider email as connected.
2. **Given** no personal connection exists, **When** WarpBot needs a Google Drive or Calendar tool, **Then** WarpBot receives a `connection_required` result and shows a connect CTA instead of fabricating an answer.

---

### User Story 3 - Execute MCP Tools Through WarpBot (Priority: P1)

As a user, I want to ask WarpBot to search/read Drive files or create Calendar events so that I can work with Google Drive and Calendar from the chat window.

**Why this priority**: This is the core product value: natural language actions through WarpBot backed by installed MCP tools.

**Independent Test**: Can be tested by asking WarpBot to search Google Drive and then create a Google Calendar event with explicit confirmation.

**Acceptance Scenarios**:

1. **Given** Google Drive & Calendar is installed and connected by the asking account, **When** the user asks WarpBot to search Drive, **Then** WarpBot calls the backend MCP execution endpoint and answers from sanitized provider results.
2. **Given** Google Drive & Calendar is installed and connected by the asking account, **When** the user asks WarpBot to read a supported Drive file, **Then** WarpBot calls `google_drive_get_file` and answers from sanitized metadata and bounded text content.
3. **Given** Google Drive & Calendar is installed and connected by the asking account, **When** the user asks WarpBot to create a Calendar event, **Then** WarpBot asks for confirmation before the write action is executed.
4. **Given** a write tool request has not been confirmed, **When** the AI worker attempts execution, **Then** the backend returns `confirmation_required` and no provider write occurs.

---

### User Story 4 - Browse Plugin Marketplace UI (Priority: P2)

As a user, I want a Plugins screen inspired by the reference UI so that I can discover installed and available plugins without leaving WarpTalk.

**Why this priority**: The plugin capability needs a user-facing management surface, but the first backend flow can be tested without the full visual polish.

**Independent Test**: Can be tested by loading the plugins page, searching the dynamic catalog, and verifying the UI state changes after install/connect/disconnect.

**Acceptance Scenarios**:

1. **Given** the backend returns a plugin catalog, **When** the user opens the Plugins screen, **Then** the UI renders plugin rows and installed icons from API data, not a hardcoded list.
2. **Given** the user searches the plugin list, **When** the query matches plugin name or description, **Then** the filtered list updates without changing the data source.
3. **Given** the reference UI has Public/Personal tabs, **When** WarpTalk implements this screen, **Then** WarpTalk does not include Public/Personal tabs and instead shows one unified plugin catalog.

## Edge Cases

- Google Drive & Calendar is installed but the current user has not connected a Google account.
- User connects Google successfully but later revokes provider access.
- Stored access token expires and refresh succeeds.
- Stored refresh token is invalid or revoked.
- MCP server returns a provider rate limit or unavailable error.
- Provider result contains sensitive fields that should not be passed to the model or frontend.
- A plugin is disabled while a WarpBot conversation is open.
- A user tries to execute a tool in a workspace where `AllowAnyPlugins` is false.
- A user tries to execute a tool from an account that has not personally installed the plugin.
- A write action confirmation is replayed or belongs to another conversation/user.

## Requirements

### Functional Requirements

- **FR-565-001**: System MUST maintain a backend plugin catalog that describes available MCP app plugins and their tool capabilities.
- **FR-565-002**: System MUST expose plugin catalog and installation status through `/api/v1/assistant/plugins` APIs.
- **FR-565-003**: System MUST store plugin installation/disable state at personal account scope, not workspace scope.
- **FR-565-004**: System MUST allow users to connect and disconnect their own provider account only after their account installed the plugin.
- **FR-565-005**: System MUST encrypt OAuth credentials at rest and never expose credentials to `warptalk-ai` or `warptalk-web`.
- **FR-565-006**: System MUST expose dynamic MCP tool schemas for the current user state and active workspace policy.
- **FR-565-007**: System MUST execute MCP tools only through an AssistantService backend endpoint using the caller's authenticated context.
- **FR-565-008**: System MUST return structured execution errors including `plugin_not_installed`, `connection_required`, `missing_scope`, `confirmation_required`, `permission_denied`, `provider_rate_limited`, and `provider_unavailable`.
- **FR-565-009**: System MUST audit every MCP tool execution attempt with workspace, user, plugin key, tool name, result status, and related assistant message/conversation when available.
- **FR-565-010**: System MUST require explicit confirmation before provider write tools are executed.
- **FR-565-011**: System MUST implement `google_workspace` as the first MCP plugin key, displayed as Google Drive & Calendar, with Drive search/read and Calendar list/create capabilities.
- **FR-565-012**: Plugin UI MUST render catalog, installed row, and actions from backend API data without hardcoded plugin rows.
- **FR-565-013**: Plugin UI MUST NOT include Public/Personal tabs in the MVP.
- **FR-565-014**: WarpBot Skills/Plugins UI MUST show installed plugin status and connection CTA when the user needs to connect a provider account.
- **FR-565-015**: Workspace settings MUST expose `AllowAnyPlugins` with default `true`; when false, WarpBot MUST NOT invoke personal plugins in that workspace.

### Key Entities

- **PluginDefinition**: Static or configured provider definition containing plugin key, label, description, icon metadata, OAuth scopes, and allowed MCP tools.
- **PluginInstallation**: Personal account record that states whether a plugin is installed or disabled for a user.
- **PluginConnection**: User-level provider account connection for an installed personal plugin, including encrypted OAuth credential fields for the MVP.
- **WorkspacePluginPolicy**: Workspace configuration field that gates whether personal plugins may be invoked inside WarpBot conversations for that workspace.
- **PluginToolAudit**: Append-only audit record for MCP tool execution attempts.
- **McpToolDescriptor**: Sanitized tool schema exposed to WarpBot for the current workspace/user state.
- **McpToolExecutionRequest**: Backend request to execute one MCP tool with validated arguments and optional confirmation metadata.

## Success Criteria

### Measurable Outcomes

- **SC-565-001**: A user can install Google Drive & Calendar and see it in their installed plugin list without a page refresh.
- **SC-565-002**: Another account in the same workspace does not inherit the first user's plugin installation or connection.
- **SC-565-003**: A connected user can ask WarpBot to search Google Drive and receive provider-backed results.
- **SC-565-004**: Calendar event creation never calls the provider until the user confirms the generated action.
- **SC-565-005**: Frontend plugin rows are fully driven by API response data; adding a backend catalog item renders a new row without editing the UI list.
- **SC-565-006**: OAuth credentials are absent from frontend payloads, AI worker logs, and tool result payloads.

## Assumptions

- The MVP starts with the `google_workspace` plugin key displayed as Google Drive & Calendar; Gmail and Docs-write capabilities can be catalogued later once provider scopes and privacy copy are ready.
- Provider OAuth setup and MCP server configuration are available through environment variables.
- `AssistantService` is the correct MVP home because WarpBot already depends on it.
- The implementation should be internally modular so it can later be extracted into an Integration Service.
- The UI is inspired by the attached Plugins screen, but WarpTalk copy and layout must fit the existing design system.
- No Public/Personal tab split is needed for MVP; all plugin install and connection state is personal.
- Workspace Owner/Admin control usage only through `AllowAnyPlugins`; they do not own or manage member OAuth tokens.
