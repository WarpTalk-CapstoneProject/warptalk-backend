# Class Diagram Specification - Transcript Module

Key classes of the Transcript module are described in the Class Specification table below.

| Class | Field / Method | Description |
| :--- | :--- | :--- |
| `Transcript` | `Id, WorkspaceId, TranslationRoomId, TranslationRoomSessionId, Version, Status, SourceLanguage, TotalSegments, TotalDurationMs, IsCurrent, PreviousTranscriptId` | Aggregate transcript header entity representing compiled meeting dialogues; uses version chaining (`PreviousTranscriptId`) and becomes immutable once finalized. |
| `TranscriptSegment` | `Id, TranscriptId, SpeakerParticipantId, SpeakerName, OriginalText, OriginalLanguage, StartTimeMs, EndTimeMs, Confidence, SequenceOrder, IsCorrected, IsFinal` | Individual speech-to-text segment captured during live translation; contains speaker attribution, timestamps, and confidence scores. |
| `TranscriptCorrection` | `Id, SegmentId, UserId, OriginalText, CorrectedText, CorrectionType, Status, TriggeredRetranslation, TranslationContentId` | Entity capturing user edits to transcript segments or translations; retains complete audit history to enable revert operations. |
| `TranscriptExport` | `Id, TranscriptId, UserId, Format, FileUrl, IncludedLanguages, CreatedAt` | Record entity tracking generated transcript export files (PDF, DOCX, TXT, VTT, SRT). |
| `TranslationContent` | `Id, WorkspaceId, TextHash, TargetLanguage, TranslatedText, TranslatorModel, Confidence, SourceSttConfidence, IsRetranslated, PreviousTranslationContentId` | Content-addressed translation cache entity deduplicated per workspace via SHA-256 `TextHash`. |
| `SegmentTranslationLink` | `SegmentId, TranslationContentId, TargetLanguage, IsCurrent, DeliveredAt` | Junction entity linking speech segments to target language translations; maintains active translation flags. |
| `AudioDubbing` | `Id, WorkspaceId, TranslationContentId, TextHash, VoiceType, Provider, ProviderVoiceId, AudioUrl, DurationMs, Status` | Synthesized audio dubbing cache entity generated via TTS or voice cloning for translated text segments. |
| `Glossary` | `Id, WorkspaceId, Name, Description, SourceLanguage, TargetLanguage, TermCount, IsActive` | Workspace-level domain glossary container defining specialized terminology pairs. |
| `GlossaryTerm` | `Id, GlossaryId, SourceTerm, TargetTerm, Context, Domain, Priority, Definition, UsageNote, PartOfSpeech, IsActive` | Individual glossary entry specifying custom translation rules for specific domain terms. |
| `GlobalGlossaryTerm` | `Id, Term, PreferredTranslation, SourceLanguage, TargetLanguage, BusinessDomain, Priority, Status, Version` | System-wide global glossary entry enforced across all translation pipelines. |
| `GlobalGlossaryAudit` | `Id, TermId, Action, BeforeJson, AfterJson, ActorUserId, CreatedAt` | Audit log record capturing lifecycle changes and edits to global glossary terms. |
| `TranscriptsController` | `GetTranscript(...), UpdateSegment(...), ExportTranscript(...), FinalizeTranscript(...)` | API controller exposing endpoints for viewing transcripts, editing segments, finalizing transcripts, and exporting files. |
| `GlossariesController` | `CreateGlossary(...), AddTerm(...), ImportTerms(...)` | API controller managing workspace glossary containers and term definitions. |
| `GlobalGlossariesController` | `CreateGlobalTerm(...), UpdateGlobalTerm(...), PublishTerm(...)` | API controller for administrative management of system-wide global glossary terms. |
| `TranscriptService` | `CompileAndGenerateArtifactsAsync(...), UpdateSegmentTextAsync(...), ExportTranscriptAsync(...)` | Application service orchestrating segment aggregation, transcript finalization, AI summary generation, and file exporting. |
| `GlobalGlossaryService` | `BulkImportAsync(...), PublishTermAsync(...), MatchGlossaryTermsAsync(...)` | Application service validating and applying glossary terms to translation text payloads. |
| `TranscriptRedisConsumer` | `ConsumeSegmentAsync(...), ConsumeMeetingEndedAsync(...)` | Asynchronous worker reading live transcript segments from Redis Streams and triggering post-meeting AI processing. |
| `ExpiredArtifactsDeleterWorker` | `DeleteExpiredArtifacts(...)` | Background worker enforcing retention policies by purging expired transcript artifacts. |
| `TranscriptDbContext` | `Transcripts, TranscriptSegments, TranscriptCorrections, TranslationContents, Glossaries, GlossaryTerms` | Entity Framework Core DbContext managing persistence for speech transcripts, segments, corrections, translations, and glossaries. |
| `UnitOfWork` | `SaveChangesAsync(), BeginTransactionAsync(), CommitTransactionAsync()` | Manages transactional consistency for multi-entity transcript operations. |
| `TranscriptRepository` | `GetByRoomIdAsync(...), AddAsync(...)` | Persistence repository for retrieving and saving meeting transcript aggregate roots. |
| `TranscriptSegmentRepository` | `GetSegmentsByTranscriptIdAsync(...), AddAsync(...)` | Persistence repository managing individual speech-to-text segments captured during sessions. |
| `GlossaryRepository` | `GetByWorkspaceIdAsync(...), AddAsync(...)` | Persistence repository managing workspace glossary containers and custom terms. |
| `OpenAI` | `CompleteAsync(...), EmbedAsync(...)` | External LLM service provider generating embeddings, text completions, and meeting summaries. |
| `VectorDatabase` | `SearchAsync(...)` | External vector database (Qdrant) indexing transcript and document chunks for semantic RAG queries. |
