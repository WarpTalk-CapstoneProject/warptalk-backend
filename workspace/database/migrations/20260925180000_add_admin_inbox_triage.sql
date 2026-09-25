-- Migration: 20260925180000_add_admin_inbox_triage
-- Ticket: G12 — internal management: the pending-work inbox (/admin/inbox)
-- Created At: 2026-09-25
-- Description:
--   The inbox itself is NOT stored: every item is read live from the service that owns it (billing,
--   auth, notification, this service's outbox) and disappears when its condition stops holding. What
--   staff add ON TOP of an item is stored here, keyed on the item's stable key
--   (e.g. 'invoice_past_due:<invoice id>'):
--
--   1. workspace.admin_inbox_item_states — who it is assigned to, until when it is snoozed, and a
--      manual "done" for items whose source has no natural completion. One row per key.
--   2. workspace.admin_inbox_notes       — internal notes on an item. Append-only, like
--      workspace_admin_notes: a correction is a new note.
--
--   Every change is also recorded in the admin audit log (entity type inbox_item).
--   Idempotent (IF NOT EXISTS). No BEGIN/COMMIT: the migration runner owns the transaction.

CREATE TABLE IF NOT EXISTS workspace.admin_inbox_item_states (
    item_key      varchar(200) PRIMARY KEY,
    item_type     varchar(60)  NOT NULL,
    assignee_id   uuid         NULL,
    assigned_by   uuid         NULL,
    assigned_at   timestamptz  NULL,
    snoozed_until timestamptz  NULL,
    snoozed_by    uuid         NULL,
    done_at       timestamptz  NULL,
    done_by       uuid         NULL,
    created_at    timestamptz  NOT NULL DEFAULT now(),
    updated_at    timestamptz  NOT NULL DEFAULT now(),
    updated_by    uuid         NULL
);

CREATE INDEX IF NOT EXISTS idx_admin_inbox_item_states_assignee
    ON workspace.admin_inbox_item_states (assignee_id)
    WHERE assignee_id IS NOT NULL;

COMMENT ON TABLE workspace.admin_inbox_item_states IS
    'G12: triage of pending-work inbox items (assignment, snooze, manual done), keyed on the item key its source service gives it. The items themselves are read live and never stored.';

CREATE TABLE IF NOT EXISTS workspace.admin_inbox_notes (
    id         uuid PRIMARY KEY DEFAULT uuidv7(),
    item_key   varchar(200) NOT NULL,
    body       text         NOT NULL,
    author_id  uuid         NOT NULL,
    created_at timestamptz  NOT NULL DEFAULT now(),
    CONSTRAINT admin_inbox_notes_body_length
        CHECK (char_length(btrim(body)) BETWEEN 1 AND 4000)
);

CREATE INDEX IF NOT EXISTS idx_admin_inbox_notes_item
    ON workspace.admin_inbox_notes (item_key, created_at DESC);

COMMENT ON TABLE workspace.admin_inbox_notes IS
    'G12: append-only internal notes on a pending-work inbox item. Platform staff only.';

GRANT SELECT, INSERT, UPDATE, DELETE
    ON workspace.admin_inbox_item_states
    TO warptalk_workspace_runtime;

-- Append-only, as workspace_admin_notes.
GRANT SELECT, INSERT
    ON workspace.admin_inbox_notes
    TO warptalk_workspace_runtime;
