# Class Diagram Specification - AI Assistant Module

Key classes of the AI Assistant module are described in the Class Specification table below.

| Class | Field / Method | Description |
| :--- | :--- | :--- |
| `AssistantConversation` | `Id, UserId, WorkspaceId, Title` | Represents a multi-turn chat thread between a user and the AI Assistant within a workspace context. |
| `AssistantMessage` | `Id, ConversationId, Role, Content` | Message turn within a thread; role indicates `user`, `assistant`, `system`, or `tool`. |
| `AssistantToolCall` | `Id, MessageId, ToolName, ArgumentsJson` | Audit record of LLM tool invocation requests (e.g., workspace document search, API calls). |
| `AssistantChatController` | `getConversations(...), getMessages(...), postMessage(...)` | Boundary controller exposing REST APIs for fetching chat history and posting user prompts. |
| `AssistantChatGrpcService` | `askAssistantStream(...)` | High-performance gRPC streaming endpoint delivering real-time LLM token streams to desktop/web clients. |
| `AssistantConversationService` | `createConversationAsync(...), sendMessageAsync(...), executeToolCallsAsync(...)` | Core application service orchestrating RAG context retrieval, LLM tool execution loops, and response generation. |
| `RedisAssistantChatRequestPublisher` | `publishChatRequestAsync(...)` | Infrastructure component queuing heavy LLM inference requests to Redis channels for asynchronous processing. |
| `OpenAiLlmService` | `generateCompletionWithToolsAsync(...)` | External LLM integration service executing model completions with function calling capabilities. |
| `QdrantVectorDb` | `searchWorkspaceEmbeddingsAsync(...)` | External vector database retrieving semantically relevant workspace document chunks for RAG context. |
| `RedisAssistantChannel` | `publishStreamChunk(...)` | Realtime Redis pub/sub channel streaming LLM token chunks back to gRPC listeners. |
