# Feature Specification: Google Meet Link Creation via WarpBot

**Feature Branch**: `feat/google-meet-warpbot-link`
**Created**: 2026-09-05
**Status**: Approved for MVP implementation
**Input**: User request to implement full Flow 3: WarpBot creates a Google Meet meeting, returns the Google Meet link, and syncs the meeting into WarpTalk Schedule as an external bridge.

## User Story

As a WarpTalk user with Google Workspace connected, I want to ask WarpBot to create a Google Meet meeting so that WarpBot can create the Google Calendar event, generate the Meet link, and return the join link in chat.

## Acceptance Criteria

1. Given Google Workspace is installed and connected, when WarpBot calls `google_calendar_create_meet_event` with summary, start, and end, then AssistantService creates a Google Calendar event with Google Meet conference data.
2. Given Google returns `hangoutLink`, then the tool result includes `provider = google_meet`, `eventId`, `calendarEventLink`, `meetLink`, and `meetLinkStatus = success`.
3. Given Google omits `hangoutLink` but returns a video entry point, then the tool result uses the entry point URI as `meetLink`.
4. Given the tool is write-effect, then existing confirmation and audit gates remain unchanged.
5. Given WarpBot receives a successful Meet tool result, then the final answer includes the exact returned `meetLink`.
6. Given WarpBot syncs the created Google Meet into WarpTalk, then TranslationRoomService stores external provider metadata on an `EXTERNAL_BRIDGE` room and exposes it through detail/list DTOs.
7. Given a Google Meet external bridge appears in web schedules, then the UI distinguishes it from a native WarpTalk room and exposes the external join URL.
8. Given desktop bridge code is present, then the web bridge type includes `openTranscriptWindow` so the external transcript popup is a typed capability.

## Scope

In scope:

- Curated native Google Workspace tool descriptor and adapter code.
- Google Calendar event creation with Meet conference data.
- AI dynamic tool handling tests for final link answer.
- External provider metadata on TranslationRoom create/list/detail payloads.
- Web display support for external Google Meet schedule rows.
- Typed desktop bridge helper for transcript popup opening.

Out of scope:

- End-user CRUD of arbitrary tool definitions.
- Remote Google MCP dynamic tool sync.
- Private WarpBot composer inside the external popup widget, tracked separately by WT-615.

## Risks

- Google Calendar API must be enabled for the OAuth client project.
- Calendar may return conference creation as pending.
- Model-driven chaining between Google tool and `create_meeting` can be less deterministic than backend orchestration.
