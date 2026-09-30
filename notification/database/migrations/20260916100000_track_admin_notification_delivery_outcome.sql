-- Migration: 20260916100000_track_admin_notification_delivery_outcome
-- Created At: 2026-09-16
-- Description:
--   Give notification.admin_notifications a real delivery outcome, and settle the rows that
--   already exist.
--
--   THE PROBLEM
--     `status` was written once, as 'Pending', by AdminNotificationMapper.ToEntity, and nothing
--     ever wrote it again. NotificationStreamConsumerService created every recipient's row and
--     acknowledged the event without touching the announcement; a dead-lettered event did not
--     touch it either. 'Sent' and 'Failed' existed only as constants. So every announcement in
--     the admin list read 'Pending' whether it had reached everybody or nobody.
--
--   WHAT THIS ADDS
--     sent_at                When the last delivery chunk was written. NULL unless 'Sent'.
--     delivered_count        Recipients with a notification row for this announcement.
--     delivery_chunk_count   Delivery events published (one per 1,000 recipients, up to 10).
--     delivered_chunk_count  Delivery events processed. 'Sent' means this reached the one above;
--                            the consumer advances both counters in one UPDATE, inside the
--                            transaction that writes the rows and the inbox receipt.
--
--   THE BACKFILL
--     Recipient rows carry payload_json = {"AdminNotificationId": "<id>"}, so what each old
--     announcement actually delivered can be counted rather than guessed:
--       every targeted user has a row             -> 'Sent', sent_at = the newest row's time
--       otherwise, and older than one hour        -> 'Failed' (it was dead-lettered or never
--                                                    published; the worker retries within minutes)
--       otherwise                                 -> left 'Pending' so a delivery still in flight
--                                                    across this deploy can finish it
--     Only 'Pending' rows are touched, so running this twice changes nothing.
--
--   No explicit transaction control: the migration runner owns the transaction.

ALTER TABLE notification.admin_notifications
    ADD COLUMN IF NOT EXISTS sent_at timestamp with time zone NULL,
    ADD COLUMN IF NOT EXISTS delivered_count integer NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS delivery_chunk_count integer NOT NULL DEFAULT 1,
    ADD COLUMN IF NOT EXISTS delivered_chunk_count integer NOT NULL DEFAULT 0;

WITH delivered AS (
    SELECT (m.payload_json ->> 'AdminNotificationId')::uuid AS admin_notification_id,
           count(DISTINCT m.user_id)::integer AS recipients,
           max(m.created_at) AS last_delivered_at
    FROM notification.notification_messages m
    WHERE m.payload_json ? 'AdminNotificationId'
      AND (m.payload_json ->> 'AdminNotificationId') ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
    GROUP BY 1
),
targeted AS (
    SELECT a.id,
           CASE WHEN jsonb_typeof(a.target_audience_data -> 'userIds') = 'array'
                THEN jsonb_array_length(a.target_audience_data -> 'userIds')
                ELSE 0
           END AS recipients
    FROM notification.admin_notifications a
    WHERE a.status = 'Pending'
)
UPDATE notification.admin_notifications a
SET delivery_chunk_count = GREATEST(1, (t.recipients + 999) / 1000),
    delivered_count = COALESCE(d.recipients, 0),
    delivered_chunk_count = CASE
        WHEN t.recipients > 0 AND COALESCE(d.recipients, 0) >= t.recipients
            THEN GREATEST(1, (t.recipients + 999) / 1000)
        ELSE COALESCE(d.recipients, 0) / 1000
    END,
    status = CASE
        WHEN t.recipients > 0 AND COALESCE(d.recipients, 0) >= t.recipients THEN 'Sent'
        WHEN a.created_at < now() - interval '1 hour' THEN 'Failed'
        ELSE a.status
    END,
    sent_at = CASE
        WHEN t.recipients > 0 AND COALESCE(d.recipients, 0) >= t.recipients THEN d.last_delivered_at
        ELSE a.sent_at
    END,
    updated_at = now()
FROM targeted t
LEFT JOIN delivered d ON d.admin_notification_id = t.id
WHERE a.id = t.id
  AND a.status = 'Pending';
