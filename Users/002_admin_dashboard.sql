-- Additive, repeatable changes for dashboard visibility, independent blocking,
-- and append-only administrative audit history. Existing users and Moodle
-- session rows are retained.
ALTER TABLE telegram_users ADD COLUMN IF NOT EXISTS is_blocked BOOLEAN NOT NULL DEFAULT FALSE;
ALTER TABLE telegram_users ADD COLUMN IF NOT EXISTS blocked_at TIMESTAMPTZ NULL;
ALTER TABLE telegram_users ADD COLUMN IF NOT EXISTS block_reason TEXT NULL;
ALTER TABLE telegram_users ADD COLUMN IF NOT EXISTS blocked_by TEXT NULL;
ALTER TABLE telegram_users ADD COLUMN IF NOT EXISTS unblocked_at TIMESTAMPTZ NULL;
ALTER TABLE telegram_users ADD COLUMN IF NOT EXISTS unblocked_by TEXT NULL;
ALTER TABLE telegram_users ADD COLUMN IF NOT EXISTS last_activity_at TIMESTAMPTZ NULL;

CREATE INDEX IF NOT EXISTS ix_telegram_users_blocked_registered
    ON telegram_users (is_blocked, registered_at DESC);

CREATE TABLE IF NOT EXISTS telegram_user_audit_log (
    id BIGSERIAL PRIMARY KEY,
    target_telegram_user_id BIGINT NOT NULL,
    action_type TEXT NOT NULL CHECK (action_type IN ('Registered','Approved','Rejected','Blocked','Unblocked','Revoked')),
    administrator TEXT NOT NULL,
    occurred_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    reason TEXT NULL,
    previous_state TEXT NULL,
    new_state TEXT NULL
);
CREATE INDEX IF NOT EXISTS ix_telegram_user_audit_occurred
    ON telegram_user_audit_log (occurred_at DESC);
CREATE INDEX IF NOT EXISTS ix_telegram_user_audit_target
    ON telegram_user_audit_log (target_telegram_user_id, occurred_at DESC);
