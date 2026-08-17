-- Migration: 20260817060000_end_rooms_held_open_by_a_phantom_host_seat
-- Ticket: WT-450
-- Created At: 2026-08-17
-- Description:
--   The deliberate backfill for rooms that were born occupied and can therefore never be ended.
--
--   THE DEFECT THIS CLEANS UP AFTER
--     TranslationRoomMapper.BuildHostParticipant seeded the host's participant row CONNECTED at
--     room CREATION. CONNECTED is the sole seat-holding status
--     (TranslationRoomParticipantStatuses.SeatHolding), so every room claimed an occupant from
--     the moment it existed, and both reapers read exactly that:
--
--       * IdleRoomMonitoringWorker  — hasConnectedParticipants was true, so it skipped the room.
--       * AbandonedRoomSweepWorker  — AbandonedRoomPolicy.Decide saw seatHolders > 0 and
--                                     answered Leave, on every sweep, forever.
--
--     A socket is the only thing that ever clears that row (MarkParticipantDisconnectedAsync
--     runs off the Gateway hub's OnDisconnectedAsync), so a host who pressed "Join meeting" —
--     which calls /start and writes IN_PROGRESS — and then closed the tab before the hub
--     connected left a room that was IN_PROGRESS, unoccupied, and permanently unsweepable.
--
--     The code fix seeds INVITED instead, but it only helps rooms created AFTER it ships. Rows
--     already carrying the phantom seat stay stuck. That is what this file is for.
--
--   WHY TWO BUCKETS AND NOT ONE
--     AbandonedRoomSweepWorker deliberately ignores rooms older than a 7-day lookback, and says
--     why: ending one "would republish its artifacts into Knowledge as if it just happened.
--     Anything older than this needs a deliberate backfill, not a background sweep." So the two
--     halves of the backlog want opposite treatment, and this migration gives it to them:
--
--       BUCKET A — beyond the sweeper's reach. Ended HERE, in SQL, precisely so the artifacts of
--                  a meeting from three weeks ago are not re-dated into Knowledge as today's.
--                  This mirrors EndTranslationRoomAsync's writes exactly (status, ended_at,
--                  updated_at; close the open session; release CONNECTED/WAITING participants)
--                  and nothing else — the realtime publish and artifact finalization it also does
--                  are for an audience that left long ago.
--
--       BUCKET B — within the sweeper's reach. NOT ended here. Only the phantom seat is cleared,
--                  which is the single thing standing between these rooms and the sweeper. It
--                  then ends them through the service, with every side effect intact. Doing it
--                  in SQL instead would be the second implementation of "what ending means" that
--                  AbandonedRoomSweepWorker's header explicitly warns against.
--
--   WHY THIS CANNOT END A LIVE MEETING
--     Four guards, and bucket B needs all of them because it acts on rooms that could in
--     principle still be running:
--
--       1. Only role = 'HOST' rows are touched. Every attendee's seat is left alone, so a
--          meeting with anybody actually in it keeps seatHolders > 0 and is invisible to the
--          sweeper regardless of what the host's row says.
--       2. Only rooms untouched for a day (translation_rooms.updated_at).
--       3. Only rooms where NO participant row has been touched for a day either. A meeting
--          people are in gets participant writes; one nobody has been near for 24h does not.
--       4. SCHEDULED is never in scope. A future booking has nobody in it by definition and is
--          not abandoned — the same line AbandonedRoomSweepWorker draws.
--
--     Bucket B also does not itself end anything: after this runs, such a room still has to be
--     observed empty by two separate sweeps 20 minutes apart before the service ends it. Anyone
--     who rejoins in the meantime re-acquires the seat through JoinTranslationRoomAsync and the
--     grace is discarded.
--
--   Timestamps are timestamptz throughout this schema (see 20260814030000), so bare now() is
--   correct here and needs no AT TIME ZONE.
--
--   Re-running is harmless: every predicate excludes the state the statement produces.

-- Bucket A: live-status rooms the sweeper's 7-day lookback can never reach.
-- Captured up front because the UPDATE below changes the very status these predicates select on.
CREATE TEMP TABLE wt450_rooms_to_end ON COMMIT DROP AS
SELECT id
FROM translation_room.translation_rooms
WHERE status IN ('IN_PROGRESS', 'WAITING', 'PAUSED')
  AND deleted_at IS NULL
  AND started_at IS NOT NULL
  AND started_at <= now() - INTERVAL '7 days';

-- Bucket B: live-status rooms the sweeper CAN see, but which its seat count says are occupied
-- when the only occupant is the seed. Only the host row, only where nothing has stirred in a day.
CREATE TEMP TABLE wt450_seats_to_clear ON COMMIT DROP AS
SELECT p.id
FROM translation_room.translation_room_participants p
JOIN translation_room.translation_rooms r ON r.id = p.translation_room_id
WHERE r.status IN ('IN_PROGRESS', 'WAITING', 'PAUSED')
  AND r.deleted_at IS NULL
  AND (r.started_at IS NULL OR r.started_at > now() - INTERVAL '7 days')
  AND r.updated_at < now() - INTERVAL '1 day'
  AND p.role = 'HOST'
  AND p.status = 'CONNECTED'
  AND NOT EXISTS (
      SELECT 1
      FROM translation_room.translation_room_participants recent
      WHERE recent.translation_room_id = r.id
        AND recent.updated_at >= now() - INTERVAL '1 day'
  );

-- ── Bucket A, in EndTranslationRoomAsync's own order ──────────────────────────

-- Release whoever the room still claims. DISCONNECTED, not LEFT: nobody departed, the room was
-- closed around them — the same distinction MarkParticipantDisconnectedAsync draws.
UPDATE translation_room.translation_room_participants
SET status = 'DISCONNECTED',
    updated_at = now()
WHERE translation_room_id IN (SELECT id FROM wt450_rooms_to_end)
  AND status IN ('CONNECTED', 'WAITING');

-- Close whatever translation session is still open, so it gets an ended_at like any other.
UPDATE translation_room.translation_room_sessions
SET status = 'ENDED',
    ended_at = COALESCE(ended_at, now()),
    updated_at = now()
WHERE translation_room_id IN (SELECT id FROM wt450_rooms_to_end)
  AND status IN ('ACTIVE', 'PAUSED');

-- ended_at is stamped now rather than back-dated to the last sign of life. It records when the
-- room was closed, which is this migration; inventing a plausible end time would put a guess
-- into a column every duration and report reads as fact.
UPDATE translation_room.translation_rooms
SET status = 'ENDED',
    ended_at = COALESCE(ended_at, now()),
    updated_at = now()
WHERE id IN (SELECT id FROM wt450_rooms_to_end);

-- ── Bucket B: hand the room to the sweeper, let the service decide the rest ───

UPDATE translation_room.translation_room_participants
SET status = 'DISCONNECTED',
    updated_at = now()
WHERE id IN (SELECT id FROM wt450_seats_to_clear);

-- Said out loud, because a backfill that reports nothing is indistinguishable from one that
-- matched nothing.
DO $$
DECLARE
    ended_count integer;
    cleared_count integer;
BEGIN
    SELECT count(*) INTO ended_count FROM wt450_rooms_to_end;
    SELECT count(*) INTO cleared_count FROM wt450_seats_to_clear;

    RAISE NOTICE 'WT-450 backfill: ended % room(s) beyond the sweep lookback; cleared % phantom host seat(s) for the sweeper.',
        ended_count, cleared_count;
END $$;
