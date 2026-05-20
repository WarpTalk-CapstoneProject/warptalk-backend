# Tasks: 5.1 Design MeetingService schema and lifecycle

**Spec Reference**: 066-meetingservice-schema-lifecycle/spec.md
**Status**: done

## Phase 1: Requirement and boundary mapping
- `[x]` Read WT-109 objective, scope, and acceptance criteria.
- `[x]` Confirm no additional Linear comments/attachments change scope.
- `[x]` Define clear ownership boundary between MeetingService and LiveKit runtime state.

## Phase 2: Schema and lifecycle definition
- `[x]` Map existing MeetingService entities and table schema.
- `[x]` Document room, participant, and track lifecycle checkpoints.
- `[x]` Document runtime-vs-durable state separation rules.

## Phase 3: Verification and handoff
- `[x]` Cross-check docs against current source (`MeetingDbContext`, `MeetingRoomService`, `MeetingWebhookService`).
- `[x]` Run MeetingService build for baseline verification.
- `[x]` Prepare concise handoff notes for downstream implementation tickets (WT-110+).
