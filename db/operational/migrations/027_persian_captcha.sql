-- 027_persian_captcha.sql
--
-- The Persian captcha (ADR-026). MCI's red line asks for it after three failed sign-ins, and after five
-- failures elsewhere (FR-NSM-204/00 §2-3 row 3); the owner asked for it to be built now (2026-11-26).
--
-- TWO TABLES, BOTH SHORT-LIVED. A challenge lives ten minutes and is answered once; a failure counts for
-- fifteen minutes. The session sweeper removes both once they lapse.
--
-- NOTHING IN THEM CAN BE READ BACK. A challenge is found by the hash of its id and holds the hash of its
-- answer bound to that id, so a copy of the table answers no picture. A failure is counted against the
-- hash of the address, or of the name tried - never the name itself: what people type into a username box
-- is sometimes their password.

CREATE TABLE auth.captcha_challenge (
    id_sha256      bytea       PRIMARY KEY,
    answer_sha256  bytea       NOT NULL,
    expires_at     timestamptz NOT NULL,
    created_at     timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX captcha_challenge_expires ON auth.captcha_challenge (expires_at);

-- One row per failure for each subject it counts against: the address, and the name tried. Counted the
-- same whether or not an account by that name exists, so whether a captcha is asked for says nothing
-- about which accounts there are.
CREATE TABLE auth.sign_in_failure (
    id          bigserial   PRIMARY KEY,
    scope       text        NOT NULL CHECK (scope IN ('login', 'recovery')),
    subject     bytea       NOT NULL,
    failed_at   timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX sign_in_failure_subject ON auth.sign_in_failure (scope, subject, failed_at);

COMMENT ON TABLE auth.captcha_challenge IS
    'Pictures waiting for an answer (ADR-026): hashes only, ten minutes, answered once.';
COMMENT ON TABLE auth.sign_in_failure IS
    'Recent failures by hashed address or name, which decide when the captcha is asked for (ADR-026).';
