-- Migration: 20261001090000_add_voice_enrollment_challenges
-- Ticket: WT-888 — Voice Profiles: uploaded voice is not verified (impersonation risk)
-- Created At: 2026-10-01
-- Description:
--   A voice profile may now be made only from a LIVE recording that reads a random phrase the
--   server issued moments before. Until now the only protection was the self-attested five-box
--   VOICE_PROFILE_UPLOAD consent, and the dialog accepted any audio file — so anyone could clone
--   another person from a recording of them.
--
--   voice.voice_enrollment_challenges holds each issued phrase: who it was issued to, in which
--   language, when it expires (10 minutes), and — once a recording has been checked against it —
--   when it was consumed (single use, claimed by one conditional UPDATE), what the recording was
--   heard to say, the 0..1 match score, the outcome, and the profile a passing recording became.
--   That last group is the evidence for a later "that voice is not mine" dispute.
--
--   voice_profile_id has no foreign key on purpose: it is written when the recording passes, a
--   moment before the profile it names is committed, and a profile that then fails to save must
--   not take the evidence row with it.
--
--   Existing voice profiles are untouched.
--
--   Idempotent (IF NOT EXISTS), no BEGIN/COMMIT — the migration runner owns the transaction.

CREATE TABLE IF NOT EXISTS voice.voice_enrollment_challenges (
    id               UUID PRIMARY KEY DEFAULT (uuidv7()),
    user_id          UUID NOT NULL,
    language         VARCHAR(15) NOT NULL,
    phrase           VARCHAR(500) NOT NULL,
    expires_at       TIMESTAMPTZ NOT NULL,
    consumed_at      TIMESTAMPTZ,
    outcome          VARCHAR(32),
    transcript       VARCHAR(1000),
    match_score      NUMERIC(4, 3),
    voice_profile_id UUID,
    created_at       TIMESTAMPTZ NOT NULL DEFAULT (NOW()),
    CONSTRAINT voice_enrollment_challenges_outcome_check
        CHECK (outcome IS NULL OR outcome IN ('passed', 'mismatch', 'transcription_failed')),
    CONSTRAINT voice_enrollment_challenges_match_score_check
        CHECK (match_score IS NULL OR (match_score >= 0 AND match_score <= 1))
);

-- "How many phrases has this account been issued in the last ten minutes" — the issue cap.
CREATE INDEX IF NOT EXISTS voice_enrollment_challenges_user_id_created_at_idx
    ON voice.voice_enrollment_challenges (user_id, created_at);

COMMENT ON TABLE voice.voice_enrollment_challenges IS
    'WT-888: read-aloud phrases a live voice-profile recording must say. Single use, 10-minute '
    'expiry; once consumed, the row keeps the transcript, match score and outcome as evidence.';

COMMENT ON COLUMN voice.voice_enrollment_challenges.user_id IS
    'External AuthService user id. No physical FK.';

COMMENT ON COLUMN voice.voice_enrollment_challenges.consumed_at IS
    'Set once, by a conditional UPDATE, when a recording is checked against this phrase — '
    'whichever way the check goes.';

COMMENT ON COLUMN voice.voice_enrollment_challenges.match_score IS
    'Similarity 0..1 between phrase and transcript (VoiceChallengeMatcher); passes at >= 0.75.';

COMMENT ON COLUMN voice.voice_enrollment_challenges.voice_profile_id IS
    'The profile a passing recording became. No FK: written just before that profile commits.';
