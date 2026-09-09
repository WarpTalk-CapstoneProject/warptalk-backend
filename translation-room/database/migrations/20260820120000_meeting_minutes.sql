-- Biên bản họp: the meeting record a person signs, as distinct from the summary a model wrote.
--
-- WHY A TABLE RATHER THAN ANOTHER ARTIFACT TYPE
--     translation_room_artifacts holds outputs: a file was produced, here it is. Every column on
--     it describes production — format, size, url, retention. A minutes document is not an output,
--     it is a record with a LIFECYCLE: drafted, edited, signed by a named secretary, approved by
--     the chair, and from that moment immutable. None of that fits a row whose only state is
--     "active".
--
-- WHAT MAKES IT A BIÊN BẢN AND NOT A PRETTIER SUMMARY
--     Standard form — Vietnamese practice and Robert's Rules agree on the substance — requires
--     four things a summary has never carried: an IDENTITY (a number and a date of record),
--     PEOPLE WHO ARE ANSWERABLE (a chair and a secretary, named), ATTENDANCE (who was there, who
--     was invited and absent, whether that made quorum), and a SIGNED STATE. The columns below are
--     those four things; the narrative lives in `content`.
--
-- WHY THE AI IS NOT THE SECRETARY
--     `drafted_by_engine` records the machine that produced the DRAFT.
--     `secretary_participant_id` records the human who read it and is answerable for it. They are
--     deliberately separate columns and the second is never filled by a program. A document whose
--     recorder is a machine is not a minutes; it is a printout.
--
--     `edit_count_vs_draft` exists for the same reason: it is the evidence that a person actually
--     read the draft rather than approving it unseen. A reader can see the number.
--
-- WHY `based_on_transcript_version`
--     A second transcription pass can land AFTER minutes were approved. Silently re-deriving the
--     document would leave a signed record disagreeing with the transcript it cites. Instead the
--     minutes remember which version they were drawn from, and a newer transcript surfaces as
--     "the record changed after this was approved" — the reader decides whether to issue a
--     revision. Signed minutes are never edited in place; that is what `version` is for.

CREATE TABLE IF NOT EXISTS translation_room.meeting_minutes (
    id uuid PRIMARY KEY DEFAULT uuidv7(),

    translation_room_id uuid NOT NULL
        REFERENCES translation_room.translation_rooms (id),
    -- External AuthService workspace id. No physical FK, matching every other cross-service
    -- reference in this schema.
    workspace_id uuid NOT NULL,

    -- Human-facing identity, unique within a workspace: BB-<year>-<sequence>.
    minutes_no varchar(64) NOT NULL,

    -- DRAFT -> IN_REVIEW -> APPROVED. A superseded version keeps its own APPROVED status and
    -- surrenders only `is_current`, so the history of what was signed stays readable.
    status varchar(20) NOT NULL DEFAULT 'DRAFT',
    version integer NOT NULL DEFAULT 1,
    is_current boolean NOT NULL DEFAULT true,
    previous_minutes_id uuid NULL
        REFERENCES translation_room.meeting_minutes (id),

    based_on_transcript_version integer NULL,

    drafted_by_engine varchar(100) NULL,
    drafted_at timestamptz NULL,

    -- translation_room_participants.id. Nullable because a draft has not been signed yet, and
    -- because a room whose participants were purged must still render its approved minutes.
    secretary_participant_id uuid NULL,
    secretary_signed_at timestamptz NULL,
    chair_participant_id uuid NULL,
    chair_approved_at timestamptz NULL,

    edit_count_vs_draft integer NOT NULL DEFAULT 0,

    -- The structured document: opening/closing times, attendance, agenda, sections with their
    -- transcript citations, votes, decisions and action items. jsonb rather than columns because
    -- the section set comes from the summary template, which is data.
    content jsonb NOT NULL DEFAULT '{}'::jsonb,

    created_at timestamptz NOT NULL DEFAULT now(),
    created_by uuid NULL,
    updated_at timestamptz NOT NULL DEFAULT now(),
    updated_by uuid NULL
);

COMMENT ON TABLE translation_room.meeting_minutes IS
    'Biên bản họp — the signed meeting record. Distinct from translation_room_artifacts SUMMARY_EXPORT, which is what a model wrote and nobody owns.';

COMMENT ON COLUMN translation_room.meeting_minutes.drafted_by_engine IS
    'The program that produced the draft. NEVER the answerable party — see secretary_participant_id.';

COMMENT ON COLUMN translation_room.meeting_minutes.edit_count_vs_draft IS
    'How many items the secretary changed before signing. Evidence that a human read the draft; shown to the reader.';

COMMENT ON COLUMN translation_room.meeting_minutes.based_on_transcript_version IS
    'transcript.transcripts.version the draft was drawn from. A newer transcript does not rewrite signed minutes — it flags them as needing a revision.';

-- One minutes number per workspace. Also the race guard: the number is allocated by counting, so
-- two secretaries pressing at once must collide here rather than both being handed BB-2026-0007.
CREATE UNIQUE INDEX IF NOT EXISTS meeting_minutes_workspace_no_idx
    ON translation_room.meeting_minutes (workspace_id, minutes_no);

-- At most one current minutes per room, enforced the same way transcripts enforce their head
-- pointer. Without this, a failed revision leaves two rows both claiming to be the record.
CREATE UNIQUE INDEX IF NOT EXISTS meeting_minutes_one_current_per_room_idx
    ON translation_room.meeting_minutes (translation_room_id)
    WHERE is_current;

CREATE UNIQUE INDEX IF NOT EXISTS meeting_minutes_room_version_idx
    ON translation_room.meeting_minutes (translation_room_id, version);
