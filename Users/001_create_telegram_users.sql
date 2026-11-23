-- Additive and safe to rerun. The application seeds the configured administrator
-- as Approved at startup so no Telegram ID is hard-coded in this migration.
CREATE TABLE IF NOT EXISTS telegram_users (
    request_id UUID NOT NULL UNIQUE,
    telegram_user_id BIGINT PRIMARY KEY,
    first_name TEXT NULL,
    last_name TEXT NULL,
    username TEXT NULL,
    registered_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    status TEXT NOT NULL CHECK (status IN ('Pending', 'Approved', 'Rejected')),
    status_updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    decision_at TIMESTAMPTZ NULL,
    decision_by BIGINT NULL
);

CREATE INDEX IF NOT EXISTS ix_telegram_users_status_registered
    ON telegram_users (status, registered_at);
