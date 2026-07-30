-- Auth-owned one-time verification and password-reset tokens.
-- Store only SHA-256 digests; raw tokens exist only in outbound email URLs.
ALTER TABLE auth.users
    ADD COLUMN IF NOT EXISTS email_verification_token_hash varchar(64),
    ADD COLUMN IF NOT EXISTS email_verification_token_expires_at timestamptz,
    ADD COLUMN IF NOT EXISTS password_reset_token_hash varchar(64),
    ADD COLUMN IF NOT EXISTS password_reset_token_expires_at timestamptz;

CREATE UNIQUE INDEX IF NOT EXISTS users_email_verification_token_hash_key
    ON auth.users (email_verification_token_hash)
    WHERE email_verification_token_hash IS NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS users_password_reset_token_hash_key
    ON auth.users (password_reset_token_hash)
    WHERE password_reset_token_hash IS NOT NULL;
