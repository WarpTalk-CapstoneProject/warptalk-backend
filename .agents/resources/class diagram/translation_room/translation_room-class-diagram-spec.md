# Class Diagram Specification - Translation Room Module

Key classes of the Translation Room module are described in the Class Specification table below.

| Class | Field / Method | Description |
| :--- | :--- | :--- |
| `TranslationRoom` | `Id, WorkspaceId, HostId, ActiveHostId, Title, Description, TranslationRoomCode, Status, TranslationRoomType, MaxParticipants, SourceLanguage, TargetLanguages, Settings, ScheduledAt, SeriesId` | Central entity representing a translation meeting session; tracks room lifecycle status, host ownership, handover (`ActiveHostId`), and language pairing rules. |
| `TranslationRoomSeries` | `Id, WorkspaceId, HostId, RecurrenceType, RecurrenceInterval, StartTimeLocal, TimeZone, StartsOnLocalDate, EndsOnLocalDate, Status` | Booking template entity defining recurring translation room schedules and occurrence generation properties. |
| `TranslationRoomParticipant` | `Id, TranslationRoomId, UserId, DisplayName, Role, ListenLanguage, SpeakLanguage, Status, ConnectionType, IsTranslationAudioEnabled, IsUsingVoiceClone` | Tracks participant state, assigned role (`Host`, `Participant`), preferred listening/speaking languages, and audio translation toggles. |
| `TranslationRoomInvitation` | `Id, TranslationRoomId, Email, Status` | Entity managing email invitations sent to participants for a translation room. |
| `TranslationRoomSession` | `Id, TranslationRoomId, MainLanguage, AudioUrl, Status, StartedAt, EndedAt` | Entity representing active translation execution sessions within a room. |
| `TranslationRoomArtifact` | `Id, TranslationRoomId, ArtifactType, FileUrl, FileFormat, FileSizeBytes, Content, Status` | Meeting output artifact entity storing exported transcripts, summaries, or recording files generated from a session. |
| `TranslationRoomAudioRoute` | `Id, TranslationRoomId, SourceParticipantId, TargetParticipantId, SourceLanguage, TargetLanguage, VoiceCloneEnabled, Status, IsCurrent` | Defines directed point-to-point audio translation routing between a source speaker and target listener. |
| `TranslationRoomFeedback` | `Id, TranslationRoomId, UserId, OverallRating, TranslationQuality, AudioQuality, Comments` | Feedback entity collecting participant ratings on audio quality, translation accuracy, and voice cloning performance. |
| `SupportedLanguage` | `Code, Name, NativeName, IsActive` | Lookup entity defining system-supported translation languages. |
| `TranslationRoomsController` | `CreateTranslationRoom(...), StartRoom(...), JoinByLink(...), HandoffHost(...)` | REST controller managing translation room creation, session start, room code join, and host handover operations. |
| `TranslationRoomSeriesController` | `CreateSeries(...), CancelSeries(...)` | REST controller managing recurring translation room series creation and cancellation. |
| `TranslationRoomParticipantsController` | `JoinRoom(...), UpdateParticipantSettings(...), LeaveRoom(...)` | REST controller managing participant roster presence, spoken/listening language preference updates, and room exit. |
| `TranslationRoomFeedbackController` | `SubmitFeedback(...), GetRoomFeedback(...)` | REST controller handling post-meeting feedback submissions. |
| `TranslationRoomService` | `CreateTranslationRoomAsync(...), StartTranslationRoomAsync(...), EndTranslationRoomAsync(...), HandoffHostAsync(...)` | Application service orchestrating room lifecycle, state transitions, and host handover logic. |
| `TranslationRoomSeriesService` | `CreateSeriesAsync(...), MaterializeOccurrencesAsync(...)` | Application service generating room instances from recurring series templates. |
| `TranslationRoomParticipantService` | `AddParticipantAsync(...), UpdateLanguagesAsync(...), RemoveParticipantAsync(...)` | Application service managing participant roster state and language configuration updates. |
| `TranslationRoomAudioRouteService` | `ResolveAudioRoutesAsync(...), ToggleVoiceCloneAsync(...)` | Application service calculating active speaker-listener audio routing pairs. |
| `TranslationRoomDbContext` | `TranslationRooms, TranslationRoomSeries, TranslationRoomParticipants, TranslationRoomAudioRoutes, TranslationRoomSessions` | Entity Framework Core DbContext managing persistence for translation rooms, series, participants, audio routes, and sessions. |
| `UnitOfWork` | `SaveChangesAsync(), BeginTransactionAsync(), CommitTransactionAsync()` | Manages transactional consistency for multi-entity translation room operations. |
| `TranslationRoomRepository` | `GetByIdAsync(...), GetByCodeAsync(...), AddAsync(...)` | Persistence repository for retrieving translation room aggregate roots and room code lookup. |
| `TranslationRoomParticipantRepository` | `GetByRoomIdAsync(...), AddAsync(...)` | Persistence repository managing participant presence records and assigned room roles (`Host`, `Participant`). |
| `TranslationRoomAudioRouteRepository` | `GetCurrentRoutesAsync(...), AddRangeAsync(...)` | Persistence repository managing active speaker-to-listener audio translation routing pairs. |
| `AudioRouteStateMachine` | `Transition(...), CanTransition(...)` | Domain state machine governing valid status transitions for point-to-point audio translation routes. |
