-- 026_file_dates_and_archives.sql
--
-- A file's date, kept on the file, and the archive it arrived in (ADR-025).
--
-- Until 2026-11-11 a file's date lived in its name and was read again by whoever needed it: the
-- upload, to place a daily file in the queue; the TAC import, to label a version. Two things have
-- outgrown that.
--
-- THE DATE IS NOT ALWAYS IN THE FILE'S OWN NAME. GSMA delivers DeviceDatabaseTAC09Nov2026.rar and
-- the file inside is DeviceDatabase.csv: the date is on the wrapping, and gone once the file is
-- unpacked. On 2026-11-10 that catalogue was sent as the bare CSV and became version "v5124" with
-- no dataset date, so the page could not say how old the catalogue was.
--
-- AND SOMETIMES IT IS IN NO NAME AT ALL, OR IN ONE THAT CAN MEAN TWO DAYS (03-04-2026). Such a file
-- is stored and waits until a person says (the owner's choice, 2026-11-10): the answer has to be
-- kept somewhere, with who gave it.
--
-- So the date is recorded once, where it was decided, with its source. A daily file's job still
-- carries it as business_date, which is what the queue orders by. A snapshot's job does not and must
-- not - a TAC job with a business date would wait behind every earlier dataset that failed - so for
-- a snapshot this column is the only place the date is.
--
-- Null for every file registered before this migration: their dates are still read from their
-- names, as they always were.
ALTER TABLE imports.import_file
    ADD COLUMN IF NOT EXISTS declared_date        date,
    ADD COLUMN IF NOT EXISTS declared_date_source text
        CHECK (declared_date_source IN ('name', 'archive', 'person')),
    ADD COLUMN IF NOT EXISTS archive_name         text;

-- Both or neither: a date nobody is recorded as having given is the thing this migration removes.
ALTER TABLE imports.import_file
    DROP CONSTRAINT IF EXISTS ck_import_file_declared_date;
ALTER TABLE imports.import_file
    ADD CONSTRAINT ck_import_file_declared_date
        CHECK ((declared_date IS NULL) = (declared_date_source IS NULL));

COMMENT ON COLUMN imports.import_file.declared_date IS
    'The day a daily file describes, or a snapshot''s own date. Null while the file waits for a '
    'person to give it, and for files registered before migration 026.';
COMMENT ON COLUMN imports.import_file.declared_date_source IS
    'Who said so: the file''s name, the name of the archive it was the only file of, or a person.';
COMMENT ON COLUMN imports.import_file.archive_name IS
    'The archive the file was unpacked from. The archive itself is not kept: what is stored, and '
    'hashed, is the file inside, so the same file sent bare and sent packed is one file.';
