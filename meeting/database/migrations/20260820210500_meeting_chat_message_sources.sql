-- The sources a WarpBot answer in a meeting actually rests on.
--
-- The same column as assistant.assistant_messages.sources_json, on the other surface that runs the
-- same agent. Two surfaces, one mechanism: an answer's chips must survive a reload in a meeting
-- for the same reason they must in the global widget — a chip row that lives only in the realtime
-- event makes a cited answer and an uncited one look identical the moment somebody scrolls back.
--
-- Only assistant messages ever carry this. A human's chat line has no sources and leaves it NULL,
-- which is why the column is nullable rather than defaulted to an empty array: '[]' on a message
-- nobody ever asked for provenance from would be an answer to a question that was not put.
ALTER TABLE meeting.meeting_chat_messages
    ADD COLUMN IF NOT EXISTS sources_json jsonb NULL;

COMMENT ON COLUMN meeting.meeting_chat_messages.sources_json IS
    'Sources a WarpBot answer actually cited: JSON array of {marker, kind, title, ref?}. NULL on human messages and on answers that cited nothing.';
