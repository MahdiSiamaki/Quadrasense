-- 025_integration_api.sql
--
-- The integration API (ADR-024): how other systems of the estate reach Primary Inventory without a
-- person's session - Lodestone (IEH) first, Tessera (PDC) where it is registered directly - and how
-- they hand PI the facts it is told rather than observes.
--
-- Four tables, in a schema of their own:
--   client         a system allowed to call, with the scopes it may ask for and its secret, hashed;
--   access_token   a short-lived bearer token issued to a client, hashed;
--   declared_event what CRM declared through Lodestone: a SIM swap, an activation, a deactivation;
--   outbox_event   what PI tells the estate, in the order it committed, for Lodestone to pull.
--
-- Nothing here holds a key value. PI holds key references at most (CLAUDE.md, 2026-11-02), and these
-- tables hold not even those: a client secret is PI's own credential, stored as a hash.

CREATE SCHEMA integration;

-- ------------------------------------------------------------------ clients

-- A client is a system, never a person. Registered by an administrator (integration.manage), with
-- the scopes it may ask for. Its secret is 32 random bytes shown once; only the SHA-256 is kept,
-- which is enough for a secret of that entropy (MCI red lines, §2-3 row 8: secrets that need not be
-- read back are stored hashed). Deactivated, never deleted: the audit log names it.
CREATE TABLE integration.client (
    id              bigserial   PRIMARY KEY,
    client_id       text        NOT NULL,
    display_name    text        NOT NULL,
    description     text        NOT NULL DEFAULT '',
    scopes          text[]      NOT NULL,
    secret_hash     bytea       NOT NULL,
    secret_set_at   timestamptz NOT NULL DEFAULT now(),
    is_active       boolean     NOT NULL DEFAULT true,
    last_token_at   timestamptz,
    created_at      timestamptz NOT NULL DEFAULT now(),
    created_by      text        NOT NULL,
    updated_at      timestamptz NOT NULL DEFAULT now(),
    updated_by      text        NOT NULL,
    CONSTRAINT client_id_shape CHECK (client_id ~ '^[a-z][a-z0-9-]{2,47}$'),
    CONSTRAINT client_secret_length CHECK (octet_length(secret_hash) = 32)
);

CREATE UNIQUE INDEX ux_integration_client_id ON integration.client (client_id);

COMMENT ON TABLE integration.client IS
    'Systems allowed to call the integration API, with their scopes. The secret is a SHA-256 hash.';

-- A token is looked up by its hash on every request, as a session is. It names the scopes granted at
-- issue - possibly fewer than the client holds - and dies with its client: deactivating a client or
-- rotating its secret ends every token it has.
CREATE TABLE integration.access_token (
    token_hash  bytea       PRIMARY KEY,
    client_pk   bigint      NOT NULL REFERENCES integration.client (id) ON DELETE CASCADE,
    scopes      text[]      NOT NULL,
    issued_at   timestamptz NOT NULL DEFAULT now(),
    expires_at  timestamptz NOT NULL,
    CONSTRAINT access_token_hash_length CHECK (octet_length(token_hash) = 32)
);

CREATE INDEX ix_integration_token_client ON integration.access_token (client_pk);
CREATE INDEX ix_integration_token_expiry ON integration.access_token (expires_at);

-- ------------------------------------------------------------------ declared events

-- What CRM declared, delivered by Lodestone (v6: CRM -> Lodestone "orders · CRM events" -> PI
-- "events"). Declared, not observed: the binding feed shows that a number's SIM changed, this says
-- that CRM sold the change. Kept as received, keyed by PI's own identifiers; nothing here is CRM's
-- model of a customer, a contract or an order beyond an opaque reference.
--
-- The sender's event id is the idempotency key: a retry with the same id and the same content is
-- accepted again and stored once; the same id with different content is refused (payload_sha256).
CREATE TABLE integration.declared_event (
    id              bigserial   PRIMARY KEY,
    client_pk       bigint      NOT NULL REFERENCES integration.client (id),
    event_id        text        NOT NULL,
    source          text        NOT NULL,
    type            text        NOT NULL,
    occurred_at     timestamptz NOT NULL,
    received_at     timestamptz NOT NULL DEFAULT now(),
    msisdn          text        NOT NULL,
    imsi            text,
    iccid           text,
    previous_imsi   text,
    previous_iccid  text,
    reference       text,
    payload_sha256  bytea       NOT NULL,
    CONSTRAINT declared_event_type CHECK (type IN ('sim-swap', 'activation', 'deactivation')),
    CONSTRAINT declared_event_id_shape CHECK (length(event_id) BETWEEN 1 AND 128),
    CONSTRAINT declared_event_sim CHECK (imsi IS NOT NULL OR iccid IS NOT NULL),
    CONSTRAINT declared_event_swap CHECK (
        type <> 'sim-swap' OR previous_imsi IS NOT NULL OR previous_iccid IS NOT NULL)
);

CREATE UNIQUE INDEX ux_declared_event ON integration.declared_event (client_pk, event_id);
CREATE INDEX ix_declared_event_msisdn ON integration.declared_event (msisdn, occurred_at);
CREATE INDEX ix_declared_event_imsi ON integration.declared_event (imsi) WHERE imsi IS NOT NULL;
CREATE INDEX ix_declared_event_previous_imsi ON integration.declared_event (previous_imsi)
    WHERE previous_imsi IS NOT NULL;
CREATE INDEX ix_declared_event_iccid ON integration.declared_event (iccid) WHERE iccid IS NOT NULL;
CREATE INDEX ix_declared_event_previous_iccid ON integration.declared_event (previous_iccid)
    WHERE previous_iccid IS NOT NULL;

COMMENT ON TABLE integration.declared_event IS
    'SIM swaps, activations and deactivations as CRM declared them, delivered through Lodestone.';

-- ------------------------------------------------------------------ outbox

-- What PI publishes, for Lodestone to pull with a cursor: no broker and no outbound connection from
-- PI (MCI red lines, §1-4 row 4: egress has to be requested). Thin events - "the day is imported",
-- "the catalogue changed" - that a reader follows with the API for the detail.
--
-- The sequence is the cursor. Writers take an advisory lock for the rest of their transaction before
-- inserting, so sequence order is commit order and a reader that has passed a number can never see a
-- lower one appear later. Writes are a few a day; the lock costs nothing.
CREATE TABLE integration.outbox_event (
    sequence    bigserial   PRIMARY KEY,
    event_id    uuid        NOT NULL,
    type        text        NOT NULL,
    subject     text        NOT NULL,
    occurred_at timestamptz NOT NULL DEFAULT now(),
    payload     jsonb       NOT NULL
);

CREATE UNIQUE INDEX ux_outbox_event_id ON integration.outbox_event (event_id);
CREATE INDEX ix_outbox_event_occurred ON integration.outbox_event (occurred_at);

COMMENT ON TABLE integration.outbox_event IS
    'PI''s published events, in commit order. Readers resume after the sequence they last saw.';

-- ------------------------------------------------------------------ permission

-- integration.manage: registering a system, changing its scopes, rotating its secret. Dangerous: a
-- client holding inventory.read and identifiers.reveal reads every subscriber's SIM and handset.
INSERT INTO auth.permission (code, category, display_name, description, is_dangerous, sort_order)
VALUES (
    'integration.manage',
    'Administration',
    'Manage integration clients',
    'Register the systems that call the integration API (Lodestone, Tessera), choose their scopes, rotate their secrets and deactivate them. A client with inventory.read and identifiers.reveal can read every subscriber''s SIM and handset.',
    true,
    95)
ON CONFLICT (code) DO NOTHING;

INSERT INTO auth.role_permission (role_id, permission_code, granted_by)
SELECT r.id, 'integration.manage', 'migration:025'
FROM auth.role r
WHERE r.code = 'administrator'
ON CONFLICT (role_id, permission_code) DO NOTHING;
