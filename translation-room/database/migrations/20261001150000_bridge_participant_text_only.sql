-- Migration: 20261001150000_bridge_participant_text_only
-- Created At: 2026-10-01
-- Description:
--   Text-only Google Meet bridge mode (PO-approved 2026-10-01).
--
--   A bridge participant in text-only mode sits in the Meet call with their REAL microphone and
--   speakers: no VB-CABLE / Hi-Fi Cable carries a dub into Meet, because Meet already hears their
--   own voice. Synthesizing their speech into the far side's language would therefore play into
--   nothing and still spend TTS credits.
--
--   THE COLUMN
--     is_bridge_text_only   per participant, not per room: every WarpTalk user in a shared bridge
--                           room (one per Meet code) plays their OWN outbound dub into their OWN
--                           virtual cable, so whether a cable exists is a fact about one person's
--                           machine. When TRUE, that person's routes to the far-side stand-in are
--                           published to the AI workers with "TextOnly": true and tts_worker skips
--                           them. Transcript, translation text and the inbound side (far side ->
--                           this person) are unaffected.
--
--   Voice -> text may be switched at any time; text -> voice only while no translation session is
--   active (enforced in TranslationRoomService.SetBridgeAudioModeAsync).
--
--   No backfill: FALSE (voice) is what every existing row has always behaved as.
--   Re-running the file is a no-op.

ALTER TABLE translation_room.translation_room_participants
    ADD COLUMN IF NOT EXISTS is_bridge_text_only BOOLEAN NOT NULL DEFAULT FALSE;

COMMENT ON COLUMN translation_room.translation_room_participants.is_bridge_text_only IS
    'EXTERNAL_BRIDGE only: TRUE = this participant uses their real mic/speakers in Meet (text-only bridge mode); their outbound routes to the far-side stand-in get no TTS.';
