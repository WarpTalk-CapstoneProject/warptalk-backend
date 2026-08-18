-- Migration: 20260815000000_add_dub_voice_to_user_settings
-- Ticket: WT-396
-- Created At: 2026-08-15
-- Description:
--   The voice a person is DUBBED IN, which is not a thing this schema could express.
--
--   THE PROBLEM
--     `voice.voice_profiles` is already carrying two unrelated meanings. `SetPreferredVoiceAsync`
--     writes a row per language whose documented purpose is "the library voice this user HEARS" —
--     a listener preference, round-tripped into SetVoicePreference and read by the TTS worker's
--     _get_explicit_voice_choices. `CreateProfileAsync` writes a different kind of row when
--     somebody uploads a recording of themselves, which is the opposite direction: how they want
--     to SOUND.
--
--     Nothing read that second kind. The TTS worker picks the speaker's voice from exactly one
--     place — a voice cloned live from the meeting's own microphone audio — so an uploaded
--     profile was stored, listed as "active" in the UI, and never used. A tester reported the dub
--     still sounded like a stock voice after uploading their own; it did, because their upload
--     had never been connected to anything.
--
--   WHAT CHANGES
--     One nullable column on `auth.user_settings` holding the provider voice id this user should
--     be dubbed in. NULL keeps today's behaviour exactly: fall back to cloning live from the
--     meeting.
--
--   WHY A RESOLVED VOICE ID AND NOT A PROFILE REFERENCE
--     The consumer is the TTS worker, which speaks to Cartesia and wants an id. Storing a profile
--     id instead would put a join — and a second failure mode — on the hot path of every
--     utterance, to express the same fact. Resolution happens once, when the person chooses.
--
--     The cost is that deleting the profile behind a chosen voice leaves a stale id here.
--     VoiceProfileService clears it on delete for that reason; a stale id that survives anyway
--     fails the way an unknown voice already fails, by falling back.
--
--   WHY NOT REUSE voice_profiles
--     Because the ambiguity above is the bug. A column whose name says which direction it means
--     cannot be read as the other one.

ALTER TABLE auth.user_settings
    ADD COLUMN IF NOT EXISTS dub_voice_id VARCHAR(255);

COMMENT ON COLUMN auth.user_settings.dub_voice_id IS
    'Provider voice id this user is dubbed in (WT-396). NULL means clone their voice live from '
    'the meeting instead. Not to be confused with voice.voice_profiles rows written by '
    'SetPreferredVoiceAsync, which are the voice this user HEARS other people in.';
