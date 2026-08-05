# Class Diagram Specification - Translation Room Module

Key classes of the Translation Room module are described in the Class Specification table below.

| Class | Field / Method | Description |
| :--- | :--- | :--- |
| `TranslationRoom` | `RoomId, SourceLanguage, TargetLanguage, Status` | Realtime audio translation session configuration defining language pairs and pipeline execution states. |
| `TranslationRoomAudioRoute` | `RouteId, ParticipantId, InputDevice, OutputDevice` | Audio device mapping per participant; controls virtual audio driver routing (Virtual Cable vs physical mic/speakers). |
| `MeetingChatMessage` | `MessageId, RoomId, SenderId, Content` | In-room text chat payload with optional AI mention support. |
| `AudioRouteController` | `configureRoute(...), pauseTranslation(...)` | Boundary controller allowing participants to configure input/output devices and pause translation streams. |
| `MeetingHub` | `receiveSubtitle(...), playClonedAudio(...), broadcastAssistantReply(...)` | WebSocket hub streaming realtime STT subtitles, synthesized cloned audio payloads, and AI assistant responses. |
| `TranslationRoomService` | `getRoomPipelineConfig(...), pauseRoom(...), resumeRoom(...)` | Application service managing room pipeline execution state and configuration parameters. |
| `TranslationRoomAudioRouteService` | `configureRouteAsync(...), resolveOutputTarget(...)` | Application service resolving audio routing targets for participant voice streams. |
| `STTWorker` | `consumeAudioChunk(...), publishTranscribedSegment(...)` | Realtime worker consuming PCM audio chunks from Redis Streams and invoking STT transcription engines. |
| `TranslationWorker` | `translateSegment(...), applyGlossaryTerms(...)` | Worker translating transcribed text segments into target languages while applying glossary overrides. |
| `TTSWorker` | `synthesizeTranslation(...), resolveVoiceId(...)` | Worker generating cloned audio streams via Cartesia TTS APIs for translated segments. |
| `CartesiaSynthesizer` | `synthesize(...)` | External voice synthesis provider performing low-latency voice cloning and TTS audio generation. |
| `VirtualAudioDriver` | `routeToVirtualMicrophone(...)` | System-level virtual audio cable driver routing synthesized audio into meeting platforms (e.g., Google Meet). |
