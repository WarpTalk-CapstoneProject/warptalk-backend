# Class Diagram Specification - Meeting Module

Key classes of the Meeting module are described in the Class Specification table below.

| Class | Field / Method | Description |
| :--- | :--- | :--- |
| `TranslationRoom` | `RoomId, WorkspaceId, HostId, Status, ScheduledAt` | Core live translation session entity; tracks room lifecycle (Scheduled, Active, Ended) and host ownership. |
| `TranslationRoomParticipant` | `ParticipantId, RoomId, UserId, Role, Status` | Tracks participant presence in a live meeting; assigns roles (`Host`, `CoHost`, `Speaker`, `Listener`). |
| `TranslationRoomInvitation` | `InvitationId, RoomId, Email, Status` | Represents invitations issued to external or workspace participants for scheduled translation rooms. |
| `MeetingChatMessage` | `MessageId, RoomId, SenderId, Content, SentAt` | Realtime chat messages exchanged within a translation room session. |
| `TranslationRoomsController` | `createTranslationRoom(...), startRoom(...), joinByLink(...)` | Boundary controller exposing endpoints to create, launch, and join translation rooms. |
| `MeetingHub` | `broadcastRoomStatus(...), broadcastChat(...)` | SignalR WebSocket hub broadcasting room state changes, participant updates, and realtime chat. |
| `TranslationRoomService` | `createTranslationRoomAsync(...), startRoomAsync(...), endRoomAsync(...)` | Application service orchestrating meeting creation, room initialization, and session termination. |
| `TranslationRoomInvitationService` | `inviteAsync(...), acceptAsync(...), sendReminderAsync(...)` | Application service managing participant invitation dispatches and scheduled email reminders. |
| `MeetingChatService` | `sendMessageAsync(...), attachFileAsync(...)` | Application service handling chat message validation, persistence, and file attachment handling. |
| `LiveKitTokenService` | `generateJoinToken(...)` | Infrastructure service issuing WebRTC access tokens for connecting to LiveKit media servers. |
| `LiveKitServer` | `createRoom(...), issueAccessToken(...)` | External WebRTC media server hosting audio tracks and participant streams. |
