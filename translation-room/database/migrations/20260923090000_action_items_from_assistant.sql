-- Action items somebody asked for out loud, not only the ones an approved biên bản produced.
--
-- WHAT WAS WRONG
--     A person in a meeting told WarpBot "Action: ask the owner to confirm the details — owner:
--     me — deadline: none". WarpBot answered "Đã ghi nhận" with a tidy bullet list, and when asked
--     where it had been saved, admitted it had been saved nowhere. There was no way it could have
--     been: meeting_action_items rows were created ONLY by approving minutes, and
--     source_minutes_id was NOT NULL, so a commitment nobody had written into a signed document
--     had no row to become.
--
-- WHY source_minutes_id BECOMES NULLABLE RATHER THAN POINTING AT A PLACEHOLDER
--     A fake minutes row would be a document nobody wrote, numbered in the workspace's minutes
--     sequence and listed in its library. NULL says the true thing: this task did not come from
--     minutes. `source` says where it DID come from, and the check below keeps the two agreeing,
--     so a MINUTES row can never lose its document and an ASSISTANT row can never claim one.
--
-- WHY THE MINUTES LOGIC IS UNAFFECTED
--     Approval's idempotency check matches on source_minutes_id (NULL never equals a minutes id),
--     and a revision inherits status by at_ms, which an assistant row does not carry (NULL never
--     equals a citation). Neither query needed to change.
--
-- created_by
--     Who asked for the row. Minutes rows are created by the approver's click and the minutes
--     record already names the approver, so the column is only written for assistant rows — the
--     only case where "who put this task in my list" is not otherwise answerable.

ALTER TABLE translation_room.meeting_action_items
    ALTER COLUMN source_minutes_id DROP NOT NULL;

ALTER TABLE translation_room.meeting_action_items
    ADD COLUMN IF NOT EXISTS source varchar(20) NOT NULL DEFAULT 'MINUTES';

ALTER TABLE translation_room.meeting_action_items
    ADD COLUMN IF NOT EXISTS created_by uuid NULL;

ALTER TABLE translation_room.meeting_action_items
    DROP CONSTRAINT IF EXISTS meeting_action_items_source_check;

ALTER TABLE translation_room.meeting_action_items
    ADD CONSTRAINT meeting_action_items_source_check CHECK (
        (source = 'MINUTES' AND source_minutes_id IS NOT NULL)
        OR (source = 'ASSISTANT' AND source_minutes_id IS NULL)
    );

COMMENT ON COLUMN translation_room.meeting_action_items.source IS
    'MINUTES: materialised from an approved biên bản. ASSISTANT: asked for in chat and created by WarpBot on the caller''s behalf.';

COMMENT ON COLUMN translation_room.meeting_action_items.created_by IS
    'External AuthService user id of whoever asked for an ASSISTANT row. No physical FK.';

COMMENT ON TABLE translation_room.meeting_action_items IS
    'Commitments as assignable rows: from an APPROVED biên bản (source MINUTES, never from a draft), or asked for through WarpBot (source ASSISTANT).';
