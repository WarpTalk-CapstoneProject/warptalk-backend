# WT-141 Workspace-owned contracts

`WorkspaceMemberRoleChanged.v1` is emitted through RabbitMQ by the Workspace event publisher after a successful role mutation. The event carries event id, workspace id, target user id, membership type, old/new role, actor/correlation/idempotency ids, occurrence time, effective time and `next-request-or-session` behavior. Redis is intentionally not used for this event so its streams remain available for audio/realtime main-flow traffic.

This feature does not modify Notification, Gateway, Meeting/Translation Room, Transcript, AI or Auth modules. Those consumers must be delivered through follow-up specs. Until a consumer contract and E2E test exists, a setting is classified as `Persisted only / not enforced` in the Settings UI/API documentation.

Role state is written directly to the existing `WorkspaceMember.RoleId` (and, for ownership transfer, `Workspace.OwnerId` plus both member role IDs). Workspace does not add a role-change table/column or an in-memory role-change store. The apply response is the current operation receipt; durable role history and replay deduplication require a separately approved persistence change.

Role changes do not mutate the capability snapshot of a running meeting. New requests, reconnects and new sessions resolve the latest Workspace membership state.
