# Class Diagram Specification - Meeting Module

Key classes of the Meeting module are described in the Class Specification table below.

| Class | Field / Method | Description |
| :--- | :--- | :--- |
| `MeetingRoom` | `Id, TranslationRoomId, ProviderRoomName, ActiveHostId, Status, IsLocked, MuteOnEntry, ActiveEgressId, CreatedAt` | Entity representing a live WebRTC LiveKit media session bound to a translation room; manages session lock states and active egress streaming. |
| `RtcStreamParticipant` | `Id, MeetingRoomId, UserId, ProviderIdentity, DisplayName, IsActive, JoinedAt` | Entity tracking real-time WebRTC media connections (renamed from `MeetingParticipant`); represents participants streaming audio/video tracks. |
| `MeetingTrack` | `Id, MeetingParticipantId, ProviderTrackId, MediaType, IsMuted, IsActive` | Entity representing published WebRTC audio or video media tracks within an active session. |
| `RtcSessionRevocation` | `Id, MeetingRoomId, InviteeUserId, InviteeEmail, Status, ExpiresAt` | Entity tracking session bans/kick revocations (renamed from `MeetingInvitation`); acts as a deny list preventing rejected participants from rejoining. |
| `MeetingChatMessage` | `Id, MeetingRoomId, WorkspaceId, SenderUserId, SenderDisplayName, OriginalText, OriginalLanguage, SentAt` | Real-time chat message entity sent during a WebRTC session; supports text, file attachments, and AI mentions. |
| `MeetingChatTranslation` | `Id, MessageId, SourceLanguage, TargetLanguage, TranslatedText` | Translation cache entity for in-meeting chat messages; ensures fast localized chat rendering for target listeners. |
| `MeetingChatAssistantRequest` | `Id, TriggerMessageId, MeetingRoomId, WorkspaceId, RequestedByUserId, Prompt, Status` | Entity tracking inline AI assistant requests triggered from meeting chat prompts (`@WarpBot`). |
| `MeetingRoomsController` | `CreateRoom(...), LockRoom(...), EndSession(...)` | API controller exposing endpoints to create, lock/unlock, and terminate WebRTC media sessions. |
| `MeetingChatController` | `SendMessage(...), GetChatHistory(...), TranslateMessage(...)` | API controller handling in-meeting chat message submissions, history retrieval, and translation requests. |
| `MeetingHub` | `BroadcastRoomStatusAsync(...), BroadcastChatMessageAsync(...)` | SignalR WebSocket hub broadcasting real-time room state updates, chat messages, and track mute/unmute events. |
| `MeetingRoomService` | `CreateMeetingRoomAsync(...), LockRoomAsync(...), EndSessionAsync(...)` | Application service orchestrating WebRTC session initialization, provider room provisioning, and session cleanup. |
| `MeetingChatService` | `SendMessageAsync(...), TranslateMessageAsync(...)` | Application service managing chat message persistence, translation cache lookups, and AI assistant prompt dispatches. |
| `LiveKitTokenService` | `GenerateJoinTokenAsync(...)` | Infrastructure service issuing signed JWT access tokens for connecting to LiveKit media servers. |
| `MeetingDbContext` | `MeetingRooms, RtcStreamParticipants, MeetingTracks, MeetingChatMessages, MeetingChatTranslations` | Entity Framework Core DbContext managing persistence for WebRTC meeting rooms, participants, tracks, and chat entities. |
| `UnitOfWork` | `SaveChangesAsync(), BeginTransactionAsync(), CommitTransactionAsync()` | Manages database transaction boundaries across meeting repositories. |
| `MeetingRoomRepository` | `GetByIdAsync(...), GetActiveByTranslationRoomIdAsync(...), AddAsync(...)` | Persistence repository for managing active meeting room sessions. |
| `RtcStreamParticipantRepository` | `GetActiveParticipantsByRoomIdAsync(...), AddAsync(...)` | Persistence repository tracking active RTC stream participants. |
| `MeetingChatMessageRepository` | `GetByRoomIdAsync(...), AddAsync(...)` | Persistence repository managing in-meeting chat message history. |
| `LiveKitServerAdapter` | `CreateRoomAsync(...), IssueTokenAsync(...)` | Infrastructure adapter wrapping the external LiveKit Server API to manage media rooms and access tokens. |
| `LiveKitServer` | `CreateRoomAsync(...), IssueToken(...)` | External WebRTC media server platform hosting audio/video streams and managing media egress recordings. |
