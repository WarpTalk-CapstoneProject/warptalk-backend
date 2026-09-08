-- Migration: 20260908110000_mark_library_voice_picks_with_their_own_source
-- Ticket: follow-on from WT-632 (Voice Profiles)
-- Created At: 2026-09-08
-- Description:
--   Give a PICK of a public catalogue voice its own value in voice.voice_profiles.source, and
--   name the rows that already exist.
--
--   THE PROBLEM
--     This table holds two different kinds of thing. Two of the three are voices a person OWNS:
--     a recording they uploaded ('upload') and a clone captured while they spoke ('in_meeting').
--     The third is not a voice of theirs at all — it is their CHOICE of a voice from the public
--     catalogue, kept here as a pointer row so it inherits the listing and the delete path.
--
--     That third kind was written with `source` left at its 'upload' default. Nothing else in the
--     row distinguished it either: SetPreferredVoiceAsync writes provider 'cartesia', and so does
--     CollectFinishedClonesAsync when somebody's uploaded recording finishes cloning — a clone
--     lives in the Cartesia account too. The only field that still differed was display_name,
--     which was NULL for a pick and required for an upload.
--
--     So every "is this the library voice I picked?" test — all of them matching on provider and
--     language — answered yes for the person's OWN clone. The Voice Profiles page showed them
--     their own clone's provider id where the chosen voice's name belongs (a raw UUID: a personal
--     clone is not in the public catalogue and cannot be named from it), listed the pointer rows
--     among their own recordings as "Untitled profile", counted them in their voice total, and
--     offered them in the be-dubbed-in-this picker.
--
--   WHY THE NULL-NAME TEST CANNOT SIMPLY STAY
--     Because the same change that fixes the readout removes it. SetPreferredVoiceAsync now
--     stores the catalogue voice's name on the row — it already fetches the catalogue to validate
--     the pick, and that is the one moment the cache is guaranteed warm — so a pick has a name
--     from here on and "no name" stops meaning anything.
--
--   WHAT THIS DOES
--     Widens the documented vocabulary of `source` and backfills the rows already written.
--
--   WHY THE BACKFILL PREDICATE IS THIS NARROW
--     Mislabelling a real voice would hide it from its owner, so each clause has to earn itself:
--
--       source = 'upload'      — leaves 'in_meeting' rows alone; they are never picks.
--       provider = 'cartesia'  — a pick always points at the catalogue provider.
--       display_name IS NULL   — the marker, and CreateProfileAsync rejects an empty display name
--                                ("Display name is required."), so no upload can look like this.
--       embedding_ref NOT NULL — a pick always names a voice. An upload waiting on its clone has
--                                no embedding_ref, and must not be swept up while it is pending.
--       no voice_samples row   — the decisive one. An upload cannot exist without its recording:
--                                CreateProfileAsync refuses a request with no sample and writes
--                                the voice_samples row in the same transaction. A pick has none,
--                                because there is nothing of the person's to store.
--
--     Deleted sample rows still count as evidence of an upload on purpose — a person who removed
--     their sample still uploaded one, and their profile is still theirs.
--
--   REVERSIBILITY
--     Forward-only, like everything here, but harmless to re-run: the UPDATE is idempotent and
--     its predicate excludes rows it has already changed.

COMMENT ON COLUMN voice.voice_profiles.source IS
    'What this row IS: ''upload'' (a recording the person deliberately made), ''in_meeting'' '
    '(captured and cloned while they spoke), or ''library'' (their pick of a public catalogue '
    'voice, kept here as a pointer row — not a voice of theirs). Only an ''in_meeting'' row is '
    'ever replaced automatically by a better capture. Only a ''library'' row is excluded from '
    '"your voices" and from the picker that chooses how somebody is dubbed.';

UPDATE voice.voice_profiles AS p
SET source = 'library'
WHERE p.source = 'upload'
  AND p.provider = 'cartesia'
  AND p.display_name IS NULL
  AND p.embedding_ref IS NOT NULL
  AND NOT EXISTS (
      SELECT 1
      FROM voice.voice_samples AS s
      WHERE s.voice_profile_id = p.id
  );

-- At most one pick per person per language, which the application has always claimed and never
-- enforced. Partial and NOT UNIQUE deliberately: picks were stored under whichever spelling the
-- caller sent ("vi" from the library list, "vi-VN" from a room), so a duplicate pair may already
-- exist in a live database and a unique index would fail to build against it. The application now
-- normalises to the bare code and looks the row up the same way, so no new pair can be created;
-- this index is here to make the lookup cheap and to make any surviving pair visible.
CREATE INDEX IF NOT EXISTS ix_voice_profiles_library_pick_per_language
    ON voice.voice_profiles (user_id, language)
    WHERE source = 'library' AND deleted_at IS NULL;
