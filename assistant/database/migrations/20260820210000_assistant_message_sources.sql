-- The sources an assistant answer actually rests on.
--
-- Stored on the MESSAGE rather than broadcast and forgotten. A chip row that exists only in the
-- SignalR event disappears on reload, so an answer somebody comes back to reads as having no
-- provenance — which is indistinguishable from an answer that genuinely cited nothing, and that
-- is precisely the distinction the whole mechanism exists to preserve.
--
-- WHAT IS IN IT
--     A JSON array of {marker, kind, title, ref?} — see ai_assistant_worker/citations.py. It is
--     the INTERSECTION of what tools retrieved this turn and what the answer pointed at, never
--     the list of tools that ran: a semantic search returning five chunks says nothing about
--     which of them, if any, the answer used.
--
-- NULL AND '[]' ARE THE SAME THING HERE, and both mean "this answer cited nothing", which is the
-- normal case for a reply drawn from the conversation rather than a tool result. Neither means
-- "sources unknown" — an answer's citations are computed at the moment it completes, so there is
-- no third state to represent.
ALTER TABLE assistant.assistant_messages
    ADD COLUMN IF NOT EXISTS sources_json jsonb NULL;

COMMENT ON COLUMN assistant.assistant_messages.sources_json IS
    'Sources the answer actually cited: JSON array of {marker, kind, title, ref?}. NULL or [] means the answer cited nothing, which is normal.';
