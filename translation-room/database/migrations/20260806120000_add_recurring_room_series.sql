-- WT-327: recurring meeting bookings ("Daily at 08:00").
--
-- SHAPE, AND WHY
--   A series row is a BOOKING, never a meeting. Every occurrence is materialised as an ordinary
--   translation_room.translation_rooms row pointing back here through series_id. Everything
--   downstream of a room — billing, transcripts, artifacts, occupancy, the reminder sweep, the
--   AI pipeline — already assumes one room row is exactly one meeting, and this migration keeps
--   that true. Nothing downstream reads these columns.
--
--   The type/interval/by_weekdays/by_month_day quartet is deliberately wider than DAILY needs.
--   Only DAILY is materialised by the service today; WEEKLY and MONTHLY are already accepted by
--   the CHECK constraint so that adding them later is application code and a UI control, not a
--   second migration against a table that by then holds production rows.
--
-- TIME
--   start_time_local + time_zone (IANA id, never a UTC offset) is the user's actual intent, and
--   it survives a tzdata rule change because the UTC instant is re-derived per occurrence at
--   materialisation time and written to translation_rooms.scheduled_at — which stays UTC exactly
--   as it always has been.
--
-- TERMINATION
--   ends_on_local_date is NOT NULL. An indefinite series generates rooms forever for workspaces
--   nobody will open again; the application defaults it to 30 days out and caps it at 365.

CREATE TABLE IF NOT EXISTS translation_room.translation_room_series (
    id uuid PRIMARY KEY DEFAULT uuidv7(),

    -- External AuthService ids. No physical FK, consistent with translation_rooms.
    workspace_id uuid NOT NULL,
    host_id uuid NOT NULL,

    -- ── The rule ────────────────────────────────────────────────────────────
    recurrence_type varchar(20) NOT NULL DEFAULT 'DAILY',
    recurrence_interval integer NOT NULL DEFAULT 1,
    -- WEEKLY only: ISO weekday numbers, e.g. [1,3,5]. Null for DAILY.
    recurrence_by_weekdays jsonb,
    -- MONTHLY only: day of month 1-31. Null for DAILY.
    recurrence_by_month_day smallint,

    start_time_local time without time zone NOT NULL,
    -- IANA zone id, e.g. 'Asia/Ho_Chi_Minh'. Never a UTC offset: an offset cannot survive a
    -- DST rule change, and "8am daily" is a statement about the clock on the wall.
    time_zone varchar(64) NOT NULL,

    starts_on_local_date date NOT NULL,
    -- INCLUSIVE, and NOT NULL on purpose: a series must terminate.
    ends_on_local_date date NOT NULL,

    -- ── Bookkeeping ─────────────────────────────────────────────────────────
    status varchar(20) NOT NULL DEFAULT 'ACTIVE',
    -- Rolling-horizon watermark. Generation resumes strictly AFTER this date, which is what
    -- makes cancelling a single occurrence permanent: the sweep never revisits its date.
    materialized_through_local_date date,

    -- ── The template every occurrence is stamped from ────────────────────────
    title varchar(255) NOT NULL,
    description text,
    translation_room_type varchar(20) NOT NULL,
    -- 0 means "let the meeting type decide the seat count", preserved rather than frozen here.
    max_participants integer NOT NULL DEFAULT 0,
    source_language varchar(15) NOT NULL,
    target_languages jsonb NOT NULL DEFAULT '[]'::jsonb,
    settings jsonb NOT NULL DEFAULT '{}'::jsonb,
    invited_emails jsonb NOT NULL DEFAULT '[]'::jsonb,

    -- ── Audit ───────────────────────────────────────────────────────────────
    created_at timestamp NOT NULL DEFAULT now(),
    created_by uuid,
    updated_at timestamp NOT NULL DEFAULT now(),
    updated_by uuid,

    CONSTRAINT translation_room_series_recurrence_type_check
        CHECK (recurrence_type IN ('DAILY', 'WEEKLY', 'MONTHLY')),
    CONSTRAINT translation_room_series_status_check
        CHECK (status IN ('ACTIVE', 'CANCELLED', 'COMPLETED')),
    CONSTRAINT translation_room_series_interval_check
        CHECK (recurrence_interval >= 1),
    CONSTRAINT translation_room_series_month_day_check
        CHECK (recurrence_by_month_day IS NULL OR (recurrence_by_month_day BETWEEN 1 AND 31)),
    -- The termination guarantee, enforced by the database and not only by the service.
    CONSTRAINT translation_room_series_date_order_check
        CHECK (ends_on_local_date >= starts_on_local_date)
);

COMMENT ON TABLE translation_room.translation_room_series IS
    'WT-327: a recurring booking rather than a meeting - each occurrence is materialised as an ordinary translation_rooms row linked by series_id';

-- The materialisation sweep's own query: ACTIVE series whose horizon has not reached their end.
CREATE INDEX IF NOT EXISTS translation_room_series_status_materialized_through_idx
    ON translation_room.translation_room_series (status, materialized_through_local_date);

CREATE INDEX IF NOT EXISTS translation_room_series_workspace_id_created_at_idx
    ON translation_room.translation_room_series (workspace_id, created_at);

-- ── The back-reference on the room ──────────────────────────────────────────

ALTER TABLE translation_room.translation_rooms
    ADD COLUMN IF NOT EXISTS series_id uuid,
    ADD COLUMN IF NOT EXISTS series_occurrence_local_date date;

COMMENT ON COLUMN translation_room.translation_rooms.series_id IS
    'WT-327: the recurring series this room is an occurrence of, or NULL for a one-off room (which is every row that existed before this column).';
COMMENT ON COLUMN translation_room.translation_rooms.series_occurrence_local_date IS
    'WT-327: the calendar date, in the series own time zone, this occurrence was generated for.';

-- A REAL foreign key, unlike workspace_id/host_id: the series lives in this same logical
-- database and schema, so there is no cross-service boundary to respect (WT-263 dropped those).
-- RESTRICT, not CASCADE: deleting a series must never take the meetings it produced — and their
-- transcripts, artifacts and billing records — with it.
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'translation_rooms_series_id_fkey'
    ) THEN
        ALTER TABLE translation_room.translation_rooms
            ADD CONSTRAINT translation_rooms_series_id_fkey
            FOREIGN KEY (series_id)
            REFERENCES translation_room.translation_room_series (id)
            ON DELETE RESTRICT;
    END IF;
END $$;

-- THE idempotency guarantee of the materialisation sweep. One series holds at most one room per
-- local date, so a double-run, a restart mid-pass, or two service replicas sweeping at the same
-- moment cannot produce two rooms for the same day. Partial, so the millions of one-off rooms
-- with a NULL series_id are not in the index at all.
CREATE UNIQUE INDEX IF NOT EXISTS translation_rooms_series_id_occurrence_date_key
    ON translation_room.translation_rooms (series_id, series_occurrence_local_date)
    WHERE series_id IS NOT NULL;

-- The schedule's own lookup: "every future occurrence of this series", which is what a
-- series-level cancel walks.
CREATE INDEX IF NOT EXISTS translation_rooms_series_id_scheduled_at_idx
    ON translation_room.translation_rooms (series_id, scheduled_at)
    WHERE series_id IS NOT NULL;
