-- Migration: 20260923090000_add_voice_profile_clone_failure_reason
-- Ticket: voice profile "Couldn't clone" (2026-09-18 uploads)
-- Created At: 2026-09-23
-- Description:
--   Store WHY an uploaded recording could not be turned into a voice, beside the fact that it
--   could not.
--
--   THE PROBLEM
--     A failed clone was recorded as status = 'clone_failed' and nothing else. The reason came
--     back from the AI side in voice:clone_result:{profile_id}, was written to one Warning log
--     line, and the Redis key was deleted as it was read. On 2026-09-18 every upload failed at
--     the voice provider (HTTP 402 plan_upgrade_required: the Cartesia account had dropped to
--     the Free plan, which does not include voice cloning) and the next deploy took the only
--     log lines saying so. The page showed "Couldn't clone" with no way to tell an account
--     problem from a bad recording, so the one action it offered — Re-record — could not help.
--
--   clone_error_code is a stable code the page translates (PROVIDER_PLAN_REQUIRED,
--   SAMPLE_REJECTED, SAMPLE_EXPIRED, ...). clone_error is the worker's one-line detail for the
--   same failure. Both are cleared when a clone succeeds or is retried.
--
--   No backfill: the two rows that failed before this column existed have no recorded reason,
--   and inventing one would be a guess stored as a fact. They stay NULL, which the page renders
--   as "reason not recorded" and still offers the retry.

ALTER TABLE voice.voice_profiles
    ADD COLUMN IF NOT EXISTS clone_error_code VARCHAR(64);

ALTER TABLE voice.voice_profiles
    ADD COLUMN IF NOT EXISTS clone_error VARCHAR(500);

COMMENT ON COLUMN voice.voice_profiles.clone_error_code IS
    'Why the last clone of this recording failed, as a stable code from warptalk-ai '
    'tts_worker._clone_failure (e.g. PROVIDER_PLAN_REQUIRED, SAMPLE_REJECTED, SAMPLE_EXPIRED). '
    'NULL unless status = ''clone_failed''; NULL on a clone_failed row means the reason was not '
    'recorded (the row predates this column).';

COMMENT ON COLUMN voice.voice_profiles.clone_error IS
    'One-line human-readable detail for clone_error_code, written by the AI worker. Never a '
    'provider request id or stack trace.';
