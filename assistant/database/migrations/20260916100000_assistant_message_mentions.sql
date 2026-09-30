-- The @mentions a user message was sent with.
--
-- Stored on the MESSAGE, beside sources_json, for the same reason that column is: until now the
-- mentions went to the worker in the chat request and nowhere else. A conversation reopened from
-- history read "set up a meeting for me" with no sign the user had named Google Meet — the one
-- fact that explains why the answer reached for that plugin.
--
-- WHAT IS IN IT
--     A JSON array of {entityType, entityId, label, workspaceId}, byte for byte the string
--     AssistantConversationPayloadSerializer.SerializeMentions built for the same turn's chat
--     request. One serialization feeds both, so what the thread shows cannot drift from what the
--     worker was told.
--
-- NULL means the message named nothing. It also means "sent before this column existed", and the
-- two cannot be told apart. That is accepted rather than backfilled: the mentions of older turns
-- were never written anywhere this migration could read them from.
--
-- Additive and nullable, so it is safe to apply ahead of the service that writes it.
ALTER TABLE assistant.assistant_messages
    ADD COLUMN IF NOT EXISTS mentions_json jsonb NULL;

COMMENT ON COLUMN assistant.assistant_messages.mentions_json IS
    'Explicit @mentions a user message was sent with: JSON array of {entityType, entityId, label, workspaceId}. NULL when it named none, or was sent before this column existed.';
