-- Additive, idempotent schema for encrypted Playwright sessions. No credentials are stored.
CREATE TABLE IF NOT EXISTS moodle_sessions (
    telegram_user_id BIGINT PRIMARY KEY,
    student_id TEXT NOT NULL,
    encrypted_state BYTEA NOT NULL,
    nonce BYTEA NOT NULL CHECK (octet_length(nonce) = 12),
    auth_tag BYTEA NOT NULL CHECK (octet_length(auth_tag) = 16),
    key_version SMALLINT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
