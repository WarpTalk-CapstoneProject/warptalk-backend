-- Migration: 20260813090000_convert_voice_consent_status_to_varchar
-- Ticket: voice-clone consent returns 500 on grant
-- Created At: 2026-08-13
-- Description:
--   Nobody can consent to voice cloning, so nobody can be cloned.
--
--   THE FAILURE
--     POST /api/v1/auth/voice-consent/grant returns 500. The database says why:
--
--       42804: column "consent_status" is of type consent_status but expression is of type text
--
--     voice.voice_consents.consent_status is still a Postgres ENUM. The C# property is a
--     string, and AuthService builds its data source with plain UseNpgsql (Program.cs), so
--     Npgsql has no mapping for the type and sends text. Postgres refuses it. The read path
--     has the same defect and had simply never been exercised, because the table has no rows
--     — every grant this feature ever attempted died on this INSERT.
--
--   WHY VARCHAR AND NOT A MAPPED ENUM
--     This is the same decision migration 014-15-06-2026 already made for translation_room
--     and transcript, and for the same reason: these services carry status as text in C# and
--     do not register Postgres enums on the connection. `consent_status` survived only because
--     014 was scoped to two schemas and `voice` was not one of them. Converting it finishes
--     that job rather than introducing a second, contradictory convention in the one table
--     that was left behind.
--
--     The vocabulary is not lost. It moves to VoiceConsentStatuses, beside the other status
--     constants this codebase already keeps in C#, and the CHECK below keeps the database
--     refusing anything outside it — so an invalid status is still impossible to store, which
--     is the property the enum was actually providing.
--
--   THE TYPE IS LEFT IN PLACE
--     `consent_status` stays declared. transcript and translation_room's DbContexts still list
--     it in HasPostgresEnum, and dropping a type out from under a model that declares it buys
--     nothing here. It simply stops being the type of any column.

ALTER TABLE voice.voice_consents
    ALTER COLUMN consent_status TYPE VARCHAR(50) USING consent_status::VARCHAR;

-- What the enum was really for: making a wrong status unstorable. That has to survive the
-- conversion, or this migration trades a 500 for silent bad data.
ALTER TABLE voice.voice_consents
    ADD CONSTRAINT voice_consents_consent_status_check
    CHECK (consent_status IN ('GRANTED', 'REVOKED', 'EXPIRED'));
