# Class Diagram Specification - Assistant Module

Key classes of the Assistant module are described in the Class Specification table below.

| Class | Field / Method | Description |
| :--- | :--- | :--- |
| `AssistantConversation` | `Id, WorkspaceId, UserId, Title, ContextScope, CreatedAt, LastMessageAt, IsArchived` | Entity representing a multi-turn assistant chat thread scoped to a user and workspace. |
| `AssistantMessage` | `Id, ConversationId, WorkspaceId, UserId, Role, Content, ToolCallsJson, ToolResultsJson, Status, CreatedAt, CompletedAt` | Entity representing one turn in an assistant conversation; `Role` distinguishes `user`, `assistant`, `system`, or `tool` turns. |
| `AssistantToolCall` | `Id, MessageId, ToolName, ArgumentsJson, Status, ResultJson, CreatedAt, CompletedAt` | Entity recording tool calls (Function Calling) executed by the assistant for a message turn. |
| `AssistantConversationsController` | `GetConversations(...), GetMessages(...), PostMessage(...), ArchiveConversation(...)` | REST controller exposing assistant conversation thread creation, history retrieval, and user prompt submission endpoints. |
| `AssistantHub` | `JoinConversationAsync(...), LeaveConversationAsync(...)` | SignalR WebSocket hub managing real-time client group membership for assistant streaming responses. |
| `AssistantChatResultConsumerService` | `ExecuteAsync(...)` | Hosted API-layer background consumer reading assistant worker results from Redis, updating database state, and notifying clients. |
| `AssistantNotifier` | `NotifyMessageChunkAsync(...), NotifyCompletionAsync(...)` | SignalR notification adapter streaming real-time assistant response chunks and completion events to clients. |
| `AssistantConversationService` | `CreateConversationAsync(...), SendMessageAsync(...), AppendAssistantResultAsync(...), ExecuteToolCallsAsync(...)` | Core application service orchestrating conversation threads, user prompt submissions, assistant completions, and tool execution. |
| `AssistantDbContext` | `AssistantConversations, AssistantMessages, AssistantToolCalls` | Entity Framework Core DbContext managing data persistence for assistant conversations, turns, and tool calls. |
| `UnitOfWork` | `SaveChangesAsync(), BeginTransactionAsync(), CommitTransactionAsync()` | Manages transactional consistency for multi-entity assistant operations. |
| `AssistantConversationRepository` | `GetByWorkspaceAndUserAsync(...), GetByIdAsync(...), AddAsync(...)` | Persistence repository for retrieving and storing assistant conversation aggregates. |
| `AssistantMessageRepository` | `GetByConversationIdAsync(...), AddAsync(...), Update(...)` | Persistence repository managing conversation message turns. |
| `AssistantToolCallRepository` | `GetByMessageIdAsync(...), AddAsync(...)` | Persistence repository storing tool-call records associated with assistant messages. |
| `RedisAssistantChatRequestPublisher` | `PublishChatRequestAsync(...)` | Infrastructure publisher enqueuing assistant chat requests to Redis Streams for asynchronous AI worker processing. |
| `WarptalkAiWorker` | `GenerateAssistantResponseAsync(...)` | External AI worker process consuming queued chat requests, running RAG context retrieval, and generating assistant responses. |
