-- Persistent ASP.NET Data Protection keys keep dashboard cookies valid across app restarts.
-- Key XML is encrypted by the app before it is stored here.
CREATE TABLE IF NOT EXISTS aspnet_data_protection_keys (
    friendly_name TEXT PRIMARY KEY,
    key_xml TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
