-- Summary in another language, without taking anybody else's away.
--
-- WHAT THIS IS FOR
--     A room's summary is ONE row in translation_room_artifacts, and regenerating REPLACES it
--     (SummaryResultConsumerWorker: "Rewriting is defined as replacing the meeting's summary").
--     The read gate on the rewrite endpoint is ArtifactAccessHelper.HasAccessToRoomArtifacts,
--     which admits every participant — so in a room where the host published an English summary
--     and a Japanese attendee asked to read it in Japanese, the attendee's choice DESTROYED the
--     host's summary for everyone, and the host had to press the button again to get it back.
--     Two people who read different languages could take turns doing that indefinitely.
--
--     This table is the other half of the answer: the canonical summary stays exactly where it
--     is and keeps meaning "what the host published", and a reader's own (shape, language) is
--     kept HERE, beside it.
--
-- WHY A SEPARATE TABLE RATHER THAN A LANGUAGE COLUMN ON THE ARTIFACT
--     translation_room_artifacts rows are records of a DELIVERABLE: they carry file_url,
--     file_format, contains_raw_audio, consent_required, retention_until and a status somebody
--     can expire. A rendering generated so one person can read a meeting has none of that — it
--     is not downloadable, nobody consents to it, and it must never appear in the artifacts
--     list as a second summary to choose between. Widening that table would have every one of
--     those columns be permanently NULL and meaningless on most of its rows, and would put
--     rows into every existing query that reads artifacts by room.
--
-- THESE ROWS ARE A CACHE, AND THE PRODUCT DEPENDS ON THEM BEING ONE
--     Nothing here is authored, signed, or unrecoverable. Every row can be rebuilt from the
--     transcript by asking the model again — deleting one costs an LLM call, not data. That is
--     what makes the whole design honest about the decision behind it: the team chose "select
--     mới gen" (generate when somebody asks, never pre-generate every language), and this table
--     is only what stops the SECOND reader of a language paying for it again.
--
--     ON DELETE CASCADE for the same reason, and it is deliberately unlike the soft delete the
--     artifacts use: a cache of a room that is gone is not history worth keeping.
--
-- WHY THE KEY IS (room, template_key, language) AND NOT (room, language)
--     Shape and language are independent choices — the rewrite endpoint has taken both since
--     #376 — so "Standup in Japanese" and "General in Japanese" are different documents and
--     neither should evict the other.
--
-- WHY language IS '' RATHER THAN NULL FOR "AS SPOKEN"
--     "As spoken" is a real answer, not a missing one: it says the model followed the
--     transcript. In Postgres a NULL in a unique index does not conflict with another NULL, so
--     a nullable column would let the same (room, template, as-spoken) variant be inserted
--     without limit. The empty string is the same value the request carries end to end —
--     LanguageHelper.NormalizeLanguageCode returns "" for it, and SummaryRequestMessage
--     documents "empty means nobody chose".

CREATE TABLE IF NOT EXISTS translation_room.translation_room_summary_variants (
    id uuid PRIMARY KEY DEFAULT uuidv7(),

    translation_room_id uuid NOT NULL
        REFERENCES translation_room.translation_rooms (id) ON DELETE CASCADE,

    -- One of the keys ai_assistant_worker/summary_templates.py resolves: general, standup,
    -- interview, demo, technical, traceable. Stored as sent rather than as an enum: the set is
    -- data on the AI side and a new template must not need a migration here.
    template_key varchar(64) NOT NULL,

    -- Bare ISO 639-1, already normalised by the caller ('vi-VN' arrives as 'vi'). '' means the
    -- summary follows the transcript.
    language varchar(16) NOT NULL DEFAULT '',

    -- The same JSON shape the canonical summary artifact stores, so one parser reads both.
    content jsonb NOT NULL DEFAULT '{}'::jsonb,

    -- Who asked for it FIRST. Kept for support ("who generated this?"), never for access: a
    -- variant is readable by anyone who may read the room's artifacts, exactly like the
    -- canonical summary it sits beside. It is not private to its requester.
    created_by uuid NULL,

    created_at timestamptz NOT NULL DEFAULT now(),

    -- Moved on every regeneration, which is what makes staleness answerable for a variant the
    -- same way it already is for the canonical artifact: the web compares it against the
    -- transcript's own segments (isSummaryStale). Without it a variant generated before a
    -- correction would read as current forever.
    updated_at timestamptz NOT NULL DEFAULT now()
);

-- The uniqueness that makes this a cache rather than an append-only log. Regenerating the same
-- (shape, language) must overwrite, or a room that is read in three languages for a year
-- accumulates a row per press.
CREATE UNIQUE INDEX IF NOT EXISTS ux_summary_variants_room_template_language
    ON translation_room.translation_room_summary_variants (translation_room_id, template_key, language);
