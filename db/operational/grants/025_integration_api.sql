-- 025_integration_api.sql
--
-- The application role's grants on the `integration` schema, added by migration 025 (ADR-024).
--
-- A separate file for the reason 005_device_catalog.sql gives: migrations describe the shape of the
-- database and are immutable once applied; grants describe who may do what.
--
-- Run once per environment, AFTER migration 025, as the schema owner:
--
--   psql -U sqm -d sqm -f db/operational/grants/025_integration_api.sql
--
-- Re-running is safe.

\set ON_ERROR_STOP on

GRANT USAGE ON SCHEMA integration TO sqm_app;

-- A client is registered, changed and deactivated, never deleted: the audit log names it.
GRANT SELECT, INSERT, UPDATE ON integration.client TO sqm_app;
GRANT USAGE, SELECT ON SEQUENCE integration.client_id_seq TO sqm_app;

-- Tokens expire and are swept; rotating a secret deletes a client's tokens.
GRANT SELECT, INSERT, DELETE ON integration.access_token TO sqm_app;

-- A declared event is history: received once, never changed or removed by the application.
GRANT SELECT, INSERT ON integration.declared_event TO sqm_app;
GRANT USAGE, SELECT ON SEQUENCE integration.declared_event_id_seq TO sqm_app;

-- The outbox is appended to and read; events past their retention are deleted by the sweeper.
GRANT SELECT, INSERT, DELETE ON integration.outbox_event TO sqm_app;
GRANT USAGE, SELECT ON SEQUENCE integration.outbox_event_sequence_seq TO sqm_app;
