-- WT-653. Keep every meeting that has ALREADY ENDED readable by the people who were in it.
--
-- WHY THIS RUNS AT ALL
--     `artifact_access` has existed all along and defaults to HOST_ONLY, but the transcript was
--     never gated on it: TranscriptService reaches rooms only through translation_room.proto, and
--     that contract carried no settings. So in practice every participant has been able to read
--     the transcript of every meeting they attended, for as long as the product has existed.
--
--     #344 closed that, correctly. What it could not do on its own is stop the fix reaching
--     backwards: with the gate in place and nothing else done, the transcript of every past
--     meeting leaves everyone except its host, until each host goes and presses Publish on each
--     room one at a time. That is not a bug fix arriving; that is a mass revocation, and the
--     people it lands on read it as the product having lost their data.
--
--     So this runs after the gate, not with it — the window between them is the deploy, and it is
--     the reason this migration should not sit waiting behind a long review.
--
-- WHY GRANDFATHERING IS THE HONEST ANSWER AND NOT A COMPROMISE
--     Those transcripts were shared. Not by policy, but in fact: participants opened them, read
--     them, and worked from them. Retroactively marking them private does not make them unread —
--     it only removes them from the people who already saw them. The record of what was shared is
--     what actually happened, so this writes that down.
--
--     Rooms created after this point get the real policy, and the Publish button starts meaning
--     what it has always said (#344 is what made it mean anything at all).
--
-- SCOPE, DELIBERATELY NARROW
--     ENDED rooms only. A room still SCHEDULED or IN_PROGRESS has not produced a record anybody
--     has come to rely on, so it starts life under the policy like every room after it.
--
-- IDEMPOTENT
--     Only touches rooms that do not already carry an explicit level, so a re-run cannot overwrite
--     a host who has since chosen HOST_ONLY for one of these meetings.

UPDATE translation_room.translation_rooms
SET settings = jsonb_set(
        COALESCE(settings, '{}'::jsonb),
        '{artifact_access}',
        '"ALL_PARTICIPANTS"'::jsonb,
        true
    ),
    updated_at = now()
WHERE status = 'ENDED'
  AND deleted_at IS NULL
  -- Absent, not merely HOST_ONLY. A host who explicitly set HOST_ONLY made a decision, and this
  -- migration has no business reversing it — the rooms being repaired are the ones that never had
  -- the key at all, which is every room whose settings blob predates anybody touching sharing.
  AND (settings IS NULL OR NOT (settings ? 'artifact_access'));
