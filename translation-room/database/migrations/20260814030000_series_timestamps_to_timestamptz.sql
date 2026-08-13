-- Migration: 20260814030000_series_timestamps_to_timestamptz
-- Ticket: WT-327 (follow-up) / WT-371
-- Created At: 2026-08-14
-- Description:
--   Recurring series stopped materialising. Found in production logs, not in the code:
--
--     System.ArgumentException: Cannot write DateTime with Kind=Unspecified to PostgreSQL
--     type 'timestamp with time zone', only UTC is supported
--       → WT-327: failed to materialise series 019fe933-…
--
--   THE MISMATCH
--     translation_room_series was created (migration 052 / 20260806120000) with:
--
--       created_at timestamp NOT NULL DEFAULT now(),
--       updated_at timestamp NOT NULL DEFAULT now(),
--
--     — `timestamp`, no zone. Every other table in this schema uses `timestamptz`;
--     translation_rooms.created_at/updated_at are `timestamp with time zone` right beside it.
--
--   WHY THAT BREAKS WRITES AND NOT READS
--     Npgsql returns Kind=Utc for a timestamptz column and Kind=Unspecified for a bare
--     timestamp. The entity's DateTime properties are mapped as timestamptz, so reading a series
--     row loads created_at as Unspecified and writing the same row back is refused — the failing
--     statement in the log is the whole-row UPDATE the materialisation sweep issues after
--     advancing materialized_through_local_date.
--
--     So the row could be read and never updated. Every sweep re-read the same series, tried to
--     advance it, threw, and gave up: "failed to materialise series", four occurrences at a time,
--     silently, for every recurring booking in the workspace.
--
--   THE CONVERSION
--     AT TIME ZONE 'UTC' interprets the stored naive values as UTC, which is what they are:
--     TranslationRoomSeriesService writes DateTime.UtcNow (and its injected clock defaults to
--     UtcNow), and the DEFAULT now() on a `timestamp` column stores the server's UTC wall clock —
--     these containers run UTC. Reading them as local time would shift every existing series.
--
--     Idempotent: the ALTERs are guarded on the current type, so re-running this is a no-op.

BEGIN;

DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = 'translation_room'
          AND table_name = 'translation_room_series'
          AND column_name = 'created_at'
          AND data_type = 'timestamp without time zone'
    ) THEN
        ALTER TABLE translation_room.translation_room_series
            ALTER COLUMN created_at TYPE timestamptz USING created_at AT TIME ZONE 'UTC';
    END IF;

    IF EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = 'translation_room'
          AND table_name = 'translation_room_series'
          AND column_name = 'updated_at'
          AND data_type = 'timestamp without time zone'
    ) THEN
        ALTER TABLE translation_room.translation_room_series
            ALTER COLUMN updated_at TYPE timestamptz USING updated_at AT TIME ZONE 'UTC';
    END IF;
END $$;

-- The DEFAULTs are re-stated so a future INSERT that omits them stores an instant rather than a
-- naive wall clock. now() already returns timestamptz; the old columns were coercing it down.
ALTER TABLE translation_room.translation_room_series
    ALTER COLUMN created_at SET DEFAULT now(),
    ALTER COLUMN updated_at SET DEFAULT now();

COMMIT;
