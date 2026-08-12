-- Migration: 20260812090000_share_one_room_code_per_series
-- Ticket: WT-327 (follow-up)
-- Created At: 2026-08-12
-- Description:
--   One recurring booking, one room code.
--
--   THE PROBLEM
--     Every occurrence of a series minted its own code, so a daily standup running for a month
--     had thirty codes. The invite you sent on Monday opened MONDAY'S room forever — by Wednesday
--     it pointed at a meeting that had already ended. To the person who booked it, that is one
--     meeting, and one meeting has one link.
--
--   WHAT CHANGES
--     `translation_rooms_translation_room_code_key` — UNIQUE over the whole column — is replaced
--     by a PARTIAL unique index covering one-off rooms only.
--
--     The backstop is not simply dropped. A one-off room is still the only room that may hold its
--     code, which is where accidental collisions actually happen: RoomCodeGenerator mints at
--     random and ExistsByCodeAsync is the app-level check, with the index as the thing that
--     catches a race between two concurrent creates. That protection is untouched for every room
--     that is not part of a series.
--
--     For occurrences, sharing the code IS the feature, so uniqueness there would be wrong. They
--     are not left unguarded: translation_rooms_series_id_occurrence_date_key (migration 052)
--     already makes one series hold at most one room per local date, which is the invariant that
--     keeps the materialisation sweep idempotent.
--
--   WHY NOT A SELF-REFERENCE
--     A `root_room_id` pointing back at this table would be a second way to answer "which rooms
--     are one booking", and series_id already answers it. Two grouping keys for one fact is two
--     keys that can disagree, and the one that drifts is the one nothing reads on the failing path.
--
--   RESOLUTION AT READ TIME
--     TranslationRoomRepository.GetByCodeAsync now resolves a shared code to the occurrence that
--     is live, else the next due, else the most recent. Nothing outside that method had to learn
--     that codes can repeat.

ALTER TABLE translation_room.translation_rooms
    DROP CONSTRAINT IF EXISTS translation_rooms_translation_room_code_key;

DROP INDEX IF EXISTS translation_room.translation_rooms_translation_room_code_key;

CREATE UNIQUE INDEX IF NOT EXISTS translation_rooms_one_off_code_key
    ON translation_room.translation_rooms (translation_room_code)
    WHERE series_id IS NULL;

COMMENT ON INDEX translation_room.translation_rooms_one_off_code_key IS
    'WT-327: a one-off room is still the only room holding its code. Occurrences of a series share one code on purpose - see translation_rooms_series_id_occurrence_date_key for the invariant that bounds them.';

CREATE INDEX IF NOT EXISTS translation_rooms_code_lookup_idx
    ON translation_room.translation_rooms (translation_room_code)
    WHERE deleted_at IS NULL;
