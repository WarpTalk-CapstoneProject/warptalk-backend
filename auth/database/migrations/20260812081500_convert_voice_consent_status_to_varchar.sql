-- Align voice consent status persistence with the translation-room pattern:
-- store status as VARCHAR in the database and keep enums in the service layer.
-- Applies to warptalk_auth, schema voice.

ALTER TABLE voice.voice_consents
    ALTER COLUMN consent_status DROP DEFAULT;

ALTER TABLE voice.voice_consents
    ALTER COLUMN consent_status TYPE varchar(20)
    USING consent_status::text;

ALTER TABLE voice.voice_consents
    ALTER COLUMN consent_status SET DEFAULT 'GRANTED'::character varying;
