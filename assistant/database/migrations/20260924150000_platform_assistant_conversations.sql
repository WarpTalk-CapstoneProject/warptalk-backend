-- Platform-scope WarpBot: a system administrator's conversations in the admin portal.
--
-- WHY TWO NEW TABLES AND NOT A FLAG ON assistant_conversations
--     Every workspace conversation is created against a workspace id, and its tools, history and
--     retrieval are all scoped by it (workspace_id is NOT NULL on both existing tables). A
--     platform conversation has no workspace. Storing it beside workspace rows would mean either
--     a sentinel id — and Guid.Empty is exactly what an unset CreateAssistantConversationRequest
--     already sends — or a nullable workspace_id that every existing query would have to learn to
--     exclude. Separate tables make the isolation structural: no workspace query can read these
--     rows, and there is no workspace column here for a platform row to carry.
--
-- WHO WRITES THEM
--     Only AssistantService's PlatformAssistantConversationsController, which is behind the
--     WarpTalkSystemAdmin policy. Rows are private to user_id, exactly like workspace ones.
--
-- Additive: two new tables, nothing existing is touched. Safe to apply ahead of the service.
-- No BEGIN/COMMIT: the migration runner owns the transaction.

CREATE TABLE IF NOT EXISTS assistant.platform_conversations (
    id              uuid                     PRIMARY KEY,
    user_id         uuid                     NOT NULL,
    title           varchar(255)             NOT NULL DEFAULT 'New chat',
    created_at      timestamp with time zone NOT NULL DEFAULT now(),
    last_message_at timestamp with time zone NULL,
    is_archived     boolean                  NOT NULL DEFAULT false
);

CREATE INDEX IF NOT EXISTS idx_platform_conversations_user
    ON assistant.platform_conversations (user_id, last_message_at DESC);

CREATE TABLE IF NOT EXISTS assistant.platform_messages (
    id                uuid                     PRIMARY KEY,
    conversation_id   uuid                     NOT NULL
        CONSTRAINT platform_messages_conversation_id_fkey
        REFERENCES assistant.platform_conversations (id) ON DELETE CASCADE,
    user_id           uuid                     NULL,
    role              varchar(20)              NOT NULL
        CONSTRAINT platform_messages_role_check CHECK (role IN ('user', 'assistant')),
    content           text                     NOT NULL DEFAULT '',
    tool_results_json text                     NULL,
    sources_json      jsonb                    NULL,
    status            varchar(20)              NOT NULL DEFAULT 'completed',
    created_at        timestamp with time zone NOT NULL DEFAULT now(),
    completed_at      timestamp with time zone NULL
);

CREATE INDEX IF NOT EXISTS idx_platform_messages_conversation
    ON assistant.platform_messages (conversation_id, created_at);

COMMENT ON TABLE assistant.platform_conversations IS
    'Platform-scope WarpBot conversations (system admins, admin portal). No workspace column by design: kept apart from assistant_conversations so no workspace-scoped query can return them.';
COMMENT ON TABLE assistant.platform_messages IS
    'Turns of assistant.platform_conversations. sources_json holds cited admin pages ({marker, kind:"admin", title, ref}).';
