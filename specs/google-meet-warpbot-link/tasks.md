# Tasks: Google Meet Link Creation via WarpBot

## Phase 0: Tests First

- [x] Add AssistantService gateway tests for Meet event creation and link extraction.
- [x] Add TranslationRoomService tests for external metadata persistence and validation.
- [x] Add AI tests for external metadata payload and final Meet link answer.
- [x] Add web/desktop contract tests where current test structure supports them.

## Phase 1: Backend AssistantService

- [x] Add `google_calendar_create_meet_event` descriptor migration.
- [x] Implement provider adapter execution.
- [x] Normalize provider result.

## Phase 2: TranslationRoomService

- [x] Add nullable external metadata columns.
- [x] Extend entity, DTOs, mappers, and validators.

## Phase 3: AI Worker

- [x] Extend `create_meeting` arguments and payload for external metadata.
- [x] Ensure dynamic MCP flow can answer with returned `meetLink`.

## Phase 4: Web/Desktop

- [x] Add external metadata types.
- [x] Render Google Meet badge/join behavior in schedules/rooms.
- [x] Type `openTranscriptWindow` in the web desktop bridge.

## Phase 5: Verification

- [x] Run backend, AI, web, desktop targeted checks.
- [x] Run broader available suites/builds before stopping.
