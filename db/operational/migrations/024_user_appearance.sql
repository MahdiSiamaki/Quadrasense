-- 024_user_appearance.sql
--
-- How the application looks, for each person (ADR-023). One row per person who has changed
-- anything; nobody else has a row, and they get the defaults.
--
-- jsonb and not a column per setting: the settings and their allowed values live in one file the
-- API and the frontend both read (frontend/src/lib/appearance-contract.json), so a new setting is
-- a change to that file and not a migration. Nothing queries inside it, and a value the contract no
-- longer knows is ignored by the frontend, so an old row can never break a page.
--
-- In the auth schema, so the default privileges of db/operational/grants/003_least_privilege.sql
-- already give sqm_app what it needs.

CREATE TABLE auth.user_appearance (
    user_id     bigint PRIMARY KEY REFERENCES auth.user_account (id) ON DELETE CASCADE,
    settings    jsonb       NOT NULL,
    updated_at  timestamptz NOT NULL DEFAULT now()
);

COMMENT ON TABLE auth.user_appearance IS
    'Each person''s appearance settings. Validated by the API against appearance-contract.json.';
