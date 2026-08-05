# Class Diagram Specification - Transcripts and AI Module

Key classes of the Transcripts and AI module are described in the Class Specification table below.

| Class | Field / Method | Description |
| :--- | :--- | :--- |
| `Transcript` | `TranscriptId, MeetingId, Status, CompiledText` | Aggregate transcript entity representing compiled meeting dialogues and AI summaries; immutable once `COMPLETED`. |
| `TranscriptSegment` | `SegmentId, MeetingId, SpeakerId, Text` | Individual speech segment captured during live translation; contains speaker attribution and timestamped text. |
| `GlobalGlossaryTerm` | `TermId, Term, Translation, LanguagePair, Status` | Domain-specific glossary term used by translation workers to enforce standardized terminology across rooms. |
| `AssistantChatMessage` | `MessageId, WorkspaceId, Role, Content` | RAG Assistant conversation message record storing user prompts and AI responses within workspace context. |
| `WorkspaceChatController` | `askWorkspaceAssistant(...)` | Boundary controller accepting natural language questions directed at workspace knowledge bases. |
| `GlobalGlossariesController` | `bulkImport(...), publishTerm(...)` | Boundary controller for bulk importing and publishing corporate glossary terms. |
| `TranscriptServiceGrpc` | `getTranscript(...), recordSegment(...)` | High-performance gRPC endpoint for streaming speech segments and fetching compiled transcripts. |
| `TranscriptRedisConsumer` | `consumeMeetingEnded(...), consumeSegment(...)` | Asynchronous consumer listening to Redis Streams for meeting conclusion events to trigger AI summary generation. |
| `TranscriptService` | `compileAndGenerateArtifactsAsync(...), updateSegmentTextAsync(...), exportTranscriptAsync(...)` | Core service compiling speech segments into final transcripts, running AI summaries, and exporting Markdown/PDF. |
| `GlobalGlossaryService` | `bulkImportAsync(...), publishTermAsync(...)` | Application service validating and managing glossary term lifecycles and domain scopes. |
| `WorkspaceChatService` | `answerQuestionAsync(...), retrieveRelevantContext(...)` | RAG application service generating embeddings, retrieving relevant document vectors from Qdrant, and invoking LLM completions. |
| `ExpiredArtifactsDeleterWorker` | `deleteExpiredArtifacts(...)` | Background worker purging expired transcript artifacts according to workspace retention policies. |
| `OpenAI` | `complete(...), embed(...)` | External LLM service provider generating embeddings and text completions for RAG and summaries. |
| `VectorDatabase` | `search(...)` | External vector database (Qdrant) indexing workspace document chunks for semantic search. |
