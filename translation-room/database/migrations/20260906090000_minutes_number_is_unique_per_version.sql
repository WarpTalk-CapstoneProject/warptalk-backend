-- A minutes number identifies a DOCUMENT, not a row. Make the unique index say so.
--
-- WHAT WAS BROKEN
--     meeting_minutes_workspace_no_idx was UNIQUE (workspace_id, minutes_no) with no filter, while
--     MeetingMinutesService.ReviseAsync carries the number forward on purpose: a revision of
--     BB-2026-0007 is still BB-2026-0007, because renumbering it would break every reference
--     anybody had already written down. Those two statements cannot both hold. The insert of v2 hit
--     23505 every single time, so an APPROVED minutes could never be corrected — and approved
--     minutes are never edited in place, so there was no other route. The feature had never
--     completed one call.
--
-- WHY (workspace_id, minutes_no, version) AND NOT A FILTER ON is_current
--     The alternative was UNIQUE (workspace_id, minutes_no) WHERE is_current, which also lets the
--     revision in. It was rejected because it stops constraining a row the moment that row is
--     superseded: nothing would then prevent a LATER, unrelated document being numbered
--     BB-2026-0007 as well, and the workspace would hold two different chains under one number with
--     only the head of each protected. The whole point of the number is that it is a stable
--     external reference; a guarantee that evaporates when a document is revised is not one.
--
--     Widening by `version` keeps every property the old index had that anybody depended on:
--
--     * ONE CHAIN PER NUMBER. A new document is always version 1, so two chains claiming
--       BB-2026-0007 still collide at (workspace_id, 'BB-2026-0007', 1). Nothing starts at v2.
--     * THE RACE GUARD. NextMinutesNoAsync allocates by counting, so two secretaries pressing at
--       once are handed the same string; both are drafts at version 1 and the loser is still
--       rejected here, which is what CreateDraftAsync's DbUpdateException catch reports as
--       ErrorNumberCollision. That path is unchanged.
--     * UNCONDITIONAL. It holds over superseded rows too, rather than releasing them.
--
--     What it newly admits is exactly the intended case: successive versions of one document.
--     meeting_minutes_room_version_idx already keeps those versions distinct per room.
--
-- NO BACKFILL, AND NONE POSSIBLE TO NEED
--     This is a pure widening: every row that satisfied UNIQUE (workspace_id, minutes_no) also
--     satisfies UNIQUE (workspace_id, minutes_no, version). The index build cannot fail on existing
--     data. Nor can a second version exist to conflict with anything — writing one is the operation
--     that has been failing.
--
-- FORWARD FIX IF THIS IS WRONG
--     Recreate the narrow index. That re-breaks revisions, so it is only a step on the way to a
--     different scheme (for instance, numbering revisions BB-2026-0007-r2), which would need its
--     own migration to rewrite the existing rows.

CREATE UNIQUE INDEX IF NOT EXISTS meeting_minutes_workspace_no_version_idx
    ON translation_room.meeting_minutes (workspace_id, minutes_no, version);

DROP INDEX IF EXISTS translation_room.meeting_minutes_workspace_no_idx;

COMMENT ON INDEX translation_room.meeting_minutes_workspace_no_version_idx IS
    'One document per minutes number per workspace, one row per version of it. A new document is always version 1, so two chains cannot share a number and the counting-based allocation still collides here on a race.';
