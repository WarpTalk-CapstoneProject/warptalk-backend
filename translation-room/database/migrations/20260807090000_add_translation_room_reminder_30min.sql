-- Migration: 20260807090000_add_translation_room_reminder_30min
-- Ticket: WT-326
-- Created At: 2026-08-07
-- Description:
--   Adds the sent-state column for the third meeting reminder window, T-30min, alongside the
--   T-10min and T-1min columns migration 035 added for WT-14. ReminderNotificationWorker stamps
--   it right after the reminder is sent, so a restart or a slow poll never double-sends.
--
--   One column per window is the shape WT-14 established and this deliberately follows it rather
--   than generalising two columns into a windows table: three windows do not pay for the join, and
--   the sweep predicate reads all three in one indexed scan of translation_rooms.
--
--   MIRRORED. scripts/migrations/053-07-08-2026-add-translation-room-reminder-30min.sql in
--   warptalk-infrastructure carries identical statements against the legacy monolith database
--   `warptalk`, per the WT-294 mirroring rule. Edit both or neither.
--
--   ADDITIVE ONLY, and safe to apply ahead of the service containers the way deploy-release.sh
--   does: the column is nullable with no default, so existing rows read as "not yet reminded for
--   T-30min", which is exactly true, and a backend that predates WT-326 never selects it.

ALTER TABLE translation_room.translation_rooms
    ADD COLUMN IF NOT EXISTS reminder_30min_sent_at TIMESTAMPTZ NULL;

COMMENT ON COLUMN translation_room.translation_rooms.reminder_30min_sent_at IS
    'WT-326. Set once the T-30min reminder notification has been sent for this room; NULL means it has not.';
