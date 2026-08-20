-- Commitments made in a meeting, as rows somebody can be assigned and can close.
--
-- WHAT THIS REPLACES
--     A summary's action items were `{owner: string, task: string}` inside a JSON blob. `owner`
--     was a free-text name, so nothing could be assigned, notified, tracked, carried into the next
--     meeting, or closed. They were sentences that happened to be shaped like tasks — which is the
--     complaint the review actually made about the AI output being impractical, and the same thing
--     every survey of this product category reports: action items get captured and go nowhere.
--
-- WHY ROWS APPEAR ONLY WHEN THE MINUTES ARE APPROVED
--     Before approval the document is a draft, and a draft's commitments are proposals. Creating
--     work from an unsigned document would put tasks in people's lists that the meeting never
--     ratified — and then have to withdraw them if the secretary edited the line. Approval is the
--     moment the record becomes the record, so it is the moment a commitment becomes a task.
--
-- WHY BOTH owner_name AND owner_participant_id
--     `owner_name` is what the meeting SAID; it is part of the record and never overwritten.
--     `owner_participant_id` is who that turned out to be, resolved against the roster, and NULL
--     whenever the name was ambiguous or matched nobody. Keeping both means an unresolved owner
--     still reads correctly on the page instead of vanishing, and a wrong resolution can never
--     silently rewrite what was said.
--
-- WHY series_id IS DENORMALISED HERE
--     Carry-over asks "what did the last occurrence of this recurring meeting leave open" — a
--     query across rooms. Reaching it through translation_rooms on every read would make the most
--     common carry-over query a join whose selectivity depends on a column on another table.

CREATE TABLE IF NOT EXISTS translation_room.meeting_action_items (
    id uuid PRIMARY KEY DEFAULT uuidv7(),

    translation_room_id uuid NOT NULL
        REFERENCES translation_room.translation_rooms (id),
    -- External AuthService workspace id. No physical FK, like every cross-service reference here.
    workspace_id uuid NOT NULL,

    -- The approved minutes this commitment came from.
    source_minutes_id uuid NOT NULL
        REFERENCES translation_room.meeting_minutes (id),

    -- Denormalised from the room so carry-over does not join to find its own predecessor.
    series_id uuid NULL,

    task text NOT NULL,

    -- What the meeting said, kept verbatim and never overwritten by resolution.
    owner_name varchar(200) NULL,
    -- translation_room_participants.id, when the name matched exactly one person. NULL means the
    -- name was ambiguous or nobody — never "no owner was named".
    owner_participant_id uuid NULL,
    -- External AuthService user id, copied at resolution so a purged participant row does not
    -- orphan somebody's task list.
    assignee_user_id uuid NULL,

    -- Where in the meeting the commitment was made. The same citation the summary carries, and
    -- the key a revision uses to recognise a task it already created.
    at_ms bigint NULL,

    status varchar(20) NOT NULL DEFAULT 'OPEN',
    due_date date NULL,

    -- Set when this task is the continuation of one left open by an earlier meeting.
    carried_from_action_item_id uuid NULL
        REFERENCES translation_room.meeting_action_items (id),

    closed_at timestamptz NULL,
    closed_by uuid NULL,

    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now()
);

COMMENT ON TABLE translation_room.meeting_action_items IS
    'Commitments from an APPROVED biên bản, as assignable rows. Created on approval, never from a draft.';

COMMENT ON COLUMN translation_room.meeting_action_items.owner_name IS
    'What the meeting said. Part of the record — never overwritten by resolution.';

COMMENT ON COLUMN translation_room.meeting_action_items.owner_participant_id IS
    'Who that name turned out to be. NULL when ambiguous or unmatched, never as a way of saying no owner was named.';

-- Everything a person's own task list asks for, in one index.
CREATE INDEX IF NOT EXISTS meeting_action_items_assignee_status_idx
    ON translation_room.meeting_action_items (assignee_user_id, status)
    WHERE assignee_user_id IS NOT NULL;

-- The carry-over query: what did this recurring booking leave open.
CREATE INDEX IF NOT EXISTS meeting_action_items_series_status_idx
    ON translation_room.meeting_action_items (series_id, status)
    WHERE series_id IS NOT NULL;

CREATE INDEX IF NOT EXISTS meeting_action_items_room_idx
    ON translation_room.meeting_action_items (translation_room_id);

CREATE INDEX IF NOT EXISTS meeting_action_items_minutes_idx
    ON translation_room.meeting_action_items (source_minutes_id);
