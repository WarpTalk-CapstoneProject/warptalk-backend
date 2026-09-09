# Implementation Plan: Google Meet Link Creation via WarpBot

## Architecture Decision

Use the existing WT-565 curated native plugin architecture for the MVP. The tool descriptor is seeded as catalog data and the provider-specific execution lives in `GoogleWorkspaceMcpToolGateway`. WarpBot still discovers tools dynamically from AssistantService; only the Google provider adapter is provider-specific code.

## Phases

1. Backend AssistantService: add `google_calendar_create_meet_event`, implement Calendar conference creation, normalize result, and test provider behavior.
2. TranslationRoomService: add nullable external meeting metadata fields and expose them on create/list/detail DTOs.
3. AI worker: extend native meeting draft payload for external metadata and add chat-loop tests proving final answer includes the Google Meet link.
4. Web: type and render external Google Meet metadata in schedule/meeting surfaces.
5. Desktop/Web bridge: type `openTranscriptWindow` and expose a helper.
6. Verification: run targeted backend, AI, web, and desktop gates plus full relevant suites when feasible.

## Quality Gates

- Backend AssistantService build and plugin tests.
- TranslationRoomService build and tests.
- AI pytest for MCP/chat loop/meeting draft.
- Web typecheck, lint, and targeted contract tests.
- Desktop typecheck and lint.
- `git diff --check` in every touched repo.
