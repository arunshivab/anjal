-- Anjal PostgreSQL schema (v0.2.0)
--
-- The store layer keeps three concerns separated:
--   routing_rules   - maps a local-part to a webhook URL + signing secret
--   tag_grants      - time-bounded per-tag authorisations (sub-addressing)
--   inbound_messages, webhook_deliveries - persistence and diagnostics
--
-- Apply with:  psql -d anjal -f tools/sql/schema.sql
--
-- gen_random_uuid() is in PostgreSQL 13+ standard library; no extension
-- required for it. CITEXT is needed for case-insensitive matching.

CREATE EXTENSION IF NOT EXISTS citext;

BEGIN;

CREATE TABLE IF NOT EXISTS routing_rules (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    local_part      CITEXT NOT NULL,
    webhook_url     TEXT NOT NULL,
    webhook_secret  TEXT NOT NULL,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT routing_rules_local_part_unique UNIQUE (local_part)
);

CREATE TABLE IF NOT EXISTS tag_grants (
    id               UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    local_part       CITEXT NOT NULL,
    tag              CITEXT NOT NULL,
    correlation_key  TEXT NOT NULL DEFAULT '',
    created_at       TIMESTAMPTZ NOT NULL DEFAULT now(),
    expires_at       TIMESTAMPTZ NOT NULL
);

CREATE INDEX IF NOT EXISTS tag_grants_lookup_idx
    ON tag_grants (local_part, tag, expires_at);

CREATE TABLE IF NOT EXISTS inbound_messages (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    envelope_from   TEXT NOT NULL,
    envelope_to     TEXT NOT NULL,
    local_part      CITEXT NOT NULL,
    tag             CITEXT NOT NULL DEFAULT '',
    message_id      TEXT NOT NULL DEFAULT '',
    subject         TEXT NOT NULL DEFAULT '',
    received_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
    raw_bytes       BYTEA NOT NULL
);

CREATE INDEX IF NOT EXISTS inbound_messages_local_part_idx
    ON inbound_messages (local_part, received_at DESC);

CREATE TABLE IF NOT EXISTS webhook_deliveries (
    id                  UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    inbound_message_id  UUID NOT NULL REFERENCES inbound_messages(id) ON DELETE CASCADE,
    url                 TEXT NOT NULL,
    status_code         INTEGER NOT NULL DEFAULT 0,
    attempted_at        TIMESTAMPTZ NOT NULL DEFAULT now(),
    error_message       TEXT NOT NULL DEFAULT ''
);

CREATE INDEX IF NOT EXISTS webhook_deliveries_message_idx
    ON webhook_deliveries (inbound_message_id, attempted_at DESC);

-- Outbound queue (v0.3.0)
CREATE TABLE IF NOT EXISTS outbound_messages (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    envelope_from   TEXT NOT NULL,
    envelope_to     TEXT NOT NULL,
    raw_bytes       BYTEA NOT NULL,
    status          INTEGER NOT NULL DEFAULT 0,  -- 0=Pending, 1=Sending, 2=Sent, 3=Failed
    attempts        INTEGER NOT NULL DEFAULT 0,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    next_attempt_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    give_up_at      TIMESTAMPTZ NOT NULL,
    last_error      TEXT NOT NULL DEFAULT ''
);

CREATE INDEX IF NOT EXISTS outbound_messages_lease_idx
    ON outbound_messages (status, next_attempt_at)
    WHERE status = 0;

-- Outbound TLS policies (v0.5.0)
CREATE TABLE IF NOT EXISTS outbound_tls_policies (
    id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    domain      CITEXT NOT NULL UNIQUE,
    mode        INTEGER NOT NULL,  -- 0=Opportunistic, 1=Required, 2=Disabled
    updated_at  TIMESTAMPTZ NOT NULL DEFAULT now()
);

COMMIT;
