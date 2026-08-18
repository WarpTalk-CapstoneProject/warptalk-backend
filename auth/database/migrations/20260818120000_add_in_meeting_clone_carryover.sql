-- Migration: 20260818120000_add_in_meeting_clone_carryover
-- Ticket: WT-B (voice clone carries over between meetings)
-- Created At: 2026-08-18
-- Description:
--   Let a voice cloned in a meeting outlive that meeting, and let a later, better clip replace it.
--
--   THE PROBLEM
--     An in-meeting clone lives in Redis at `voice:{meeting}:{speaker}` with a 12h TTL, keyed BY
--     MEETING. The next meeting is a different key, so every meeting re-clones from scratch and
--     pays the first twenty seconds of speech in a stock catalogue voice. The provider voice
--     itself was never deleted either, so the account accumulated one per speaker per meeting
--     while WarpTalk forgot every id it had made.
--
--     There was also no way for the clone to get BETTER. tts_worker already scores each accepted
--     clip and will replace a clone once within a meeting (voice_clone_max_upgrades), but that
--     score lives in a local dict and dies with the process — so every meeting started the
--     comparison again from nothing.
--
--   WHAT CHANGES
--     Two nullable columns on `voice.voice_profiles`, which already holds "a voice belonging to
--     this person". An in-meeting clone becomes an ordinary row in that table, which is what
--     gives it — for free — the listing, the preview, the delete, and the dub-voice picker that
--     uploaded recordings already have.
--
--   WHY A SOURCE COLUMN
--     Because the two kinds of row must be told apart and nothing else in the table can do it.
--     An uploaded recording is something a person deliberately made; an in-meeting clone is
--     something the system captured while they talked. They differ in how they may be replaced
--     (a captured one is overwritten by a better capture; an uploaded one never is), and in what
--     a person expects to see when they open the page.
--
--   WHY A SCORE COLUMN, AND WHY IT IS NULLABLE
--     It is the high-water mark: the pitch-coverage score of the clip this voice was built from
--     (tts_worker/clone_sample_quality.assess_clone_sample). A later clip replaces the voice only
--     when it beats this by voice_clone_upgrade_margin, so without storing it "is this better?"
--     is unanswerable across meetings and the clone can only ever be as good as the first clip
--     that passed the gate.
--
--     NULL means "not measured", not "zero". An uploaded profile has no such score and a clone
--     made before this column existed has none either; a fabricated 0 would grade as the worst
--     possible sample and invite an immediate replacement by anything at all.
--
--   WHY NOT A SEPARATE TABLE
--     A second table would need its own delete path, its own consent join, its own listing, and
--     its own answer to "which of these am I dubbed in" — four things voice_profiles already
--     answers. The ambiguity WT-396 fixed was two DIRECTIONS sharing one table; this is not that.
--     Both kinds of row here mean the same thing: a voice that is this person's.

ALTER TABLE voice.voice_profiles
    ADD COLUMN IF NOT EXISTS source VARCHAR(20) NOT NULL DEFAULT 'upload';

ALTER TABLE voice.voice_profiles
    ADD COLUMN IF NOT EXISTS quality_score NUMERIC(4, 3);

COMMENT ON COLUMN voice.voice_profiles.source IS
    'How this voice came to exist: ''upload'' (a recording the person deliberately made) or '
    '''in_meeting'' (captured and cloned while they spoke). Defaults to ''upload'' so every '
    'pre-existing row keeps the only meaning it could have had. Only an ''in_meeting'' row is '
    'ever replaced automatically by a better capture.';

COMMENT ON COLUMN voice.voice_profiles.quality_score IS
    'Pitch-coverage score (0..1) of the clip this voice was built from — see '
    'warptalk-ai/tts_worker/clone_sample_quality.assess_clone_sample. The high-water mark a '
    'later clip must beat by TTS_VOICE_CLONE_UPGRADE_MARGIN before it replaces this voice. '
    'NULL means not measured (an upload, or a clone predating this column) and must never be '
    'read as 0, which would grade as the worst possible sample.';

-- One automatic voice per person per language. A second would make "the voice carried over from
-- last time" ambiguous, and the upsert that maintains these has to have something to conflict on.
-- Partial, so it constrains only the rows this feature owns: uploaded profiles are deliberately
-- allowed to repeat per language, and a soft-deleted row must not block a fresh capture.
CREATE UNIQUE INDEX IF NOT EXISTS ux_voice_profiles_in_meeting_per_language
    ON voice.voice_profiles (user_id, language)
    WHERE source = 'in_meeting' AND deleted_at IS NULL;
