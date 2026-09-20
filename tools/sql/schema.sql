-- Anjal PostgreSQL schema (v0.15.0)
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

-- DKIM signing keys (v0.6.0). PEM is stored in plaintext - protect at
-- the database access layer (TLS connection, restricted role grants,
-- encryption at rest).
CREATE TABLE IF NOT EXISTS dkim_keys (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    domain          CITEXT NOT NULL UNIQUE,
    selector        TEXT NOT NULL,
    private_key_pem TEXT NOT NULL,
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- SMTP submission users (v0.8.0). Authenticates clients connecting to
-- the submission port (typically 587). Passwords are stored as PBKDF2
-- hashes in the form "pbkdf2$iterations$salt-b64$hash-b64". The
-- allowed_from_domains array restricts which MAIL FROM domains the user
-- can submit as; empty array means admin authority.
CREATE TABLE IF NOT EXISTS smtp_users (
    id                   UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    username             CITEXT NOT NULL UNIQUE,
    password_pbkdf2      TEXT NOT NULL,
    allowed_from_domains TEXT[] NOT NULL DEFAULT '{}',
    enabled              BOOLEAN NOT NULL DEFAULT TRUE,
    updated_at           TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- Local domains (v0.8.0). The MTA listener refuses RCPT TO for domains
-- not in this list ("relaying denied"). Empty list means the legacy
-- "accept all RCPT" behavior - safe only on closed networks.
CREATE TABLE IF NOT EXISTS local_domains (
    id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    domain      CITEXT NOT NULL UNIQUE,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- Multi-tenant mailbox storage (v0.9.0). Message bodies live in Maildir
-- files under ANJAL_MAILDIR_ROOT/<tenant-slug>/<local_part>@<domain>/;
-- these tables hold the tenant/domain/mailbox registry and the message
-- index (metadata only, no raw bytes).
--
-- Deleting a tenant or mailbox cascades to its rows but never touches
-- the Maildir files on disk (deletion of mail data is a later release).
CREATE TABLE IF NOT EXISTS tenants (
    id            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    slug          CITEXT NOT NULL UNIQUE,
    display_name  TEXT NOT NULL DEFAULT '',
    enabled       BOOLEAN NOT NULL DEFAULT TRUE,
    created_at    TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS tenant_domains (
    id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id   UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    domain      CITEXT NOT NULL UNIQUE,
    verified    BOOLEAN NOT NULL DEFAULT TRUE,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS tenant_domains_tenant_idx
    ON tenant_domains (tenant_id);

-- Mailboxes carry their own submission credentials (virtual-user
-- pattern): the same identity receives at local_part@domain and may
-- AUTH on the submission port to send as that domain. An empty
-- password_pbkdf2 means receive-only.
CREATE TABLE IF NOT EXISTS mailboxes (
    id               UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id        UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    local_part       CITEXT NOT NULL,
    domain           CITEXT NOT NULL,
    password_pbkdf2  TEXT NOT NULL DEFAULT '',
    display_name     TEXT NOT NULL DEFAULT '',
    enabled          BOOLEAN NOT NULL DEFAULT TRUE,
    quota_bytes      BIGINT NOT NULL DEFAULT 2147483648,  -- 2 GiB soft quota
    used_bytes       BIGINT NOT NULL DEFAULT 0,
    created_at       TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at       TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT mailboxes_address_unique UNIQUE (local_part, domain)
);

CREATE INDEX IF NOT EXISTS mailboxes_tenant_idx
    ON mailboxes (tenant_id);

-- Folders: INBOX maps to the Maildir root; other names map to a
-- Maildir++ ".Name" subdirectory. Names are case-sensitive.
CREATE TABLE IF NOT EXISTS folders (
    id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    mailbox_id  UUID NOT NULL REFERENCES mailboxes(id) ON DELETE CASCADE,
    name        TEXT NOT NULL,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT folders_mailbox_name_unique UNIQUE (mailbox_id, name)
);

-- Message index. maildir_file is relative to the folder's Maildir
-- directory (e.g. "new/1726560000.M1P42Q7.host").
CREATE TABLE IF NOT EXISTS messages (
    id             UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    mailbox_id     UUID NOT NULL REFERENCES mailboxes(id) ON DELETE CASCADE,
    folder_id      UUID NOT NULL REFERENCES folders(id) ON DELETE CASCADE,
    maildir_file   TEXT NOT NULL,
    envelope_from  TEXT NOT NULL DEFAULT '',
    message_id     TEXT NOT NULL DEFAULT '',
    from_header    TEXT NOT NULL DEFAULT '',
    to_header      TEXT NOT NULL DEFAULT '',
    subject        TEXT NOT NULL DEFAULT '',
    date_header    TEXT NOT NULL DEFAULT '',
    size_bytes     BIGINT NOT NULL DEFAULT 0,
    seen           BOOLEAN NOT NULL DEFAULT FALSE,
    flagged        BOOLEAN NOT NULL DEFAULT FALSE,
    answered       BOOLEAN NOT NULL DEFAULT FALSE,
    received_at    TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS messages_folder_received_idx
    ON messages (mailbox_id, folder_id, received_at DESC);

CREATE INDEX IF NOT EXISTS messages_message_id_idx
    ON messages (mailbox_id, message_id);

-- Anti-spam (v0.11.0). Scores are computed at delivery by Anjal.Spam;
-- messages at or above the tenant's threshold are filed in Junk instead
-- of INBOX. Sender rules force INBOX (allow) or Junk (block) regardless
-- of score. Nothing here rejects mail at SMTP time.
ALTER TABLE tenants  ADD COLUMN IF NOT EXISTS spam_threshold INTEGER NOT NULL DEFAULT 5;
ALTER TABLE messages ADD COLUMN IF NOT EXISTS spam_score     INTEGER NOT NULL DEFAULT 0;

CREATE TABLE IF NOT EXISTS sender_rules (
    id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id   UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    pattern     CITEXT NOT NULL,                     -- "alice@example.com" or "@example.com"
    action      TEXT NOT NULL CHECK (action IN ('allow', 'block')),
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT sender_rules_tenant_pattern_unique UNIQUE (tenant_id, pattern)
);

-- Webmail (v0.14.0): per-mailbox theme, and an index for unread counts.
ALTER TABLE mailboxes ADD COLUMN IF NOT EXISTS theme TEXT NOT NULL DEFAULT 'paper';

CREATE INDEX IF NOT EXISTS messages_unread_idx
    ON messages (mailbox_id, folder_id)
    WHERE NOT seen;

-- Categories (v0.15.0). Two levels in one table: a row with mailbox_id NULL
-- is a tenant default shared by every mailbox; otherwise it belongs to that
-- mailbox alone. The slot carries the colour and is stored, never derived
-- from the name and never recomputed when a category is deleted.
CREATE TABLE IF NOT EXISTS categories (
    id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id   UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    mailbox_id  UUID REFERENCES mailboxes(id) ON DELETE CASCADE,
    name        TEXT NOT NULL CHECK (length(trim(name)) BETWEEN 1 AND 40),
    slot        INT  NOT NULL DEFAULT 0 CHECK (slot BETWEEN 0 AND 8),
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- One name per scope, case-insensitively. The coalesce gives tenant defaults
-- (mailbox_id NULL) a stable key, since NULL never equals NULL in an index.
CREATE UNIQUE INDEX IF NOT EXISTS categories_scope_name_idx
    ON categories (tenant_id, coalesce(mailbox_id, '00000000-0000-0000-0000-000000000000'::uuid), lower(name));

-- "File mail from this sender here." Owned by one mailbox: categorising is a
-- personal act even when the category is shared by the tenant.
CREATE TABLE IF NOT EXISTS category_rules (
    id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    mailbox_id  UUID NOT NULL REFERENCES mailboxes(id) ON DELETE CASCADE,
    pattern     TEXT NOT NULL CHECK (pattern ~ '^(@[^@[:space:]]+|[^@[:space:]]+@[^@[:space:]]+)$'),
    category_id UUID NOT NULL REFERENCES categories(id) ON DELETE CASCADE,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (mailbox_id, pattern)
);

ALTER TABLE messages ADD COLUMN IF NOT EXISTS category_id UUID REFERENCES categories(id) ON DELETE SET NULL;
ALTER TABLE messages ADD COLUMN IF NOT EXISTS has_attachments BOOLEAN NOT NULL DEFAULT false;

-- The dashboard reads a period of one mailbox, grouped several ways.
CREATE INDEX IF NOT EXISTS messages_activity_idx ON messages (mailbox_id, received_at);
CREATE INDEX IF NOT EXISTS messages_category_idx ON messages (category_id) WHERE category_id IS NOT NULL;

COMMIT;
