-- Uploaded voice profile consent contract audit fields.
-- Applies to warptalk_auth, schema voice.

ALTER TABLE voice.voice_consents
    ADD COLUMN IF NOT EXISTS contract_snapshot text,
    ADD COLUMN IF NOT EXISTS contract_hash varchar(64),
    ADD COLUMN IF NOT EXISTS own_voice_confirmed boolean NOT NULL DEFAULT false,
    ADD COLUMN IF NOT EXISTS ai_use_confirmed boolean NOT NULL DEFAULT false,
    ADD COLUMN IF NOT EXISTS synthetic_voice_acknowledged boolean NOT NULL DEFAULT false,
    ADD COLUMN IF NOT EXISTS no_impersonation_confirmed boolean NOT NULL DEFAULT false,
    ADD COLUMN IF NOT EXISTS retention_acknowledged boolean NOT NULL DEFAULT false;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'voice_consents_voice_profile_id_fkey'
          AND conrelid = 'voice.voice_consents'::regclass
    ) THEN
        ALTER TABLE voice.voice_consents
            ADD CONSTRAINT voice_consents_voice_profile_id_fkey
            FOREIGN KEY (voice_profile_id)
            REFERENCES voice.voice_profiles(id)
            NOT VALID;
    END IF;
END
$$;

ALTER TABLE voice.voice_consents
    VALIDATE CONSTRAINT voice_consents_voice_profile_id_fkey;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'voice_consents_voice_profile_upload_contract_check'
          AND conrelid = 'voice.voice_consents'::regclass
    ) THEN
        ALTER TABLE voice.voice_consents
            ADD CONSTRAINT voice_consents_voice_profile_upload_contract_check
            CHECK (
                consent_type <> 'voice_profile_upload'
                OR (
                    voice_profile_id IS NOT NULL
                    AND contract_snapshot IS NOT NULL
                    AND contract_hash IS NOT NULL
                    AND length(contract_hash) = 64
                    AND own_voice_confirmed
                    AND ai_use_confirmed
                    AND synthetic_voice_acknowledged
                    AND no_impersonation_confirmed
                    AND retention_acknowledged
                )
            )
            NOT VALID;
    END IF;
END
$$;

ALTER TABLE voice.voice_consents
    VALIDATE CONSTRAINT voice_consents_voice_profile_upload_contract_check;

CREATE INDEX IF NOT EXISTS voice_consents_voice_profile_id_status_idx
    ON voice.voice_consents (voice_profile_id, consent_status);

CREATE INDEX IF NOT EXISTS voice_consents_user_type_created_at_idx
    ON voice.voice_consents (user_id, consent_type, created_at DESC);
