-- Patch 001: persistent Yahoo throttle state. Run once on an existing database:
--   psql -d wyckoff -f patch_001_throttle.sql
-- (Already included in schema.sql for new installs; safe to run twice.)
CREATE TABLE IF NOT EXISTS throttle_state (
    id                SMALLINT    PRIMARY KEY,
    last_call_utc     TIMESTAMPTZ NULL,
    blocked_until_utc TIMESTAMPTZ NULL,
    CONSTRAINT ck_throttle_single_row CHECK (id = 1)
);
INSERT INTO throttle_state (id) VALUES (1) ON CONFLICT (id) DO NOTHING;
