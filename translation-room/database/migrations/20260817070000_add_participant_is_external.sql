-- Migration: 20260817070000_add_participant_is_external
-- Ticket: WT-446
-- Created At: 2026-08-17
-- Description:
--   Records whether a participant was outside the room's workspace when they joined.
--
--   WHY THIS IS STORED AND NOT COMPUTED ON READ
--     "Is this person external?" is answerable today — IWorkspaceMemberDirectory.IsMemberAsync
--     resolves it over gRPC to WorkspaceService. But the roster endpoint it would have to answer
--     on is polled every 3 seconds by every client in the meeting, and answering per participant
--     means one gRPC round trip per person per poll. Externality is also not a live property: it
--     is a fact about the moment someone was let in, which is exactly what the roster wants to
--     label and what usage attribution wants to bill against.
--
--   WHY false IS THE RIGHT BACKFILL
--     Every existing row predates the concept. The overwhelmingly common case is a workspace
--     member in their own workspace's room, and false is what that person is. Marking historical
--     rows external would invent a fact nobody measured; false says "not known to be external",
--     which is both true and the safe default for anything that later counts external usage.
--
--   NOT NULL with a default, so no read path has to consider a third state. The column is written
--   on every join from here on (TranslationRoomService.JoinTranslationRoomAsync).

ALTER TABLE translation_room.translation_room_participants
    ADD COLUMN IF NOT EXISTS is_external boolean NOT NULL DEFAULT false;

COMMENT ON COLUMN translation_room.translation_room_participants.is_external IS
    'True when this participant was not an active member of the room''s workspace at join time (WT-446). Resolved once on join via WorkspaceService, never recomputed on read.';
