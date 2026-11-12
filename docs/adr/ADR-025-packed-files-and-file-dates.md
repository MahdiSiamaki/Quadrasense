# ADR-025: A file may arrive packed, and every file has a date somebody settled

- **Status:** Accepted
- **Date:** 2026-11-11
- **Decided by:** the product owner asked on 2026-11-10 that the Import Center take CSV, RAR and ZIP
  and "do the rest itself", and that a file's date be read from its name however the name writes
  it. Asked, the owner chose: RAR read with SharpCompress; Gregorian dates only; a date that can be
  read two ways waits for a person and is not guessed.

## Context

GSMA delivers the TAC catalogue as `DeviceDatabaseTAC09Nov2026.rar`. The file inside is
`DeviceDatabase.csv`. Two things went wrong with it on 2026-11-10:

- **The `.rar` was sent as it came**, to the department's server. It was read as a CSV: its first
  "line" was compared with the column contract, and the import failed three times with a message
  about a constructor (a separate fault in the path that compares an unknown header, fixed beside
  this decision). Nothing told the sender that the platform takes CSV only.
- **The CSV was sent bare**, on the laptop. It imported, as version `v5124` with no dataset date:
  the date is on the archive's name and was gone once the file was unpacked by hand. The page could
  no longer say how old the catalogue was.

The date was read in two places by two rules. A daily file's day was the first `YYYY-MM-DD` in its
name; a TAC file's was `4Oct2026`. Anything else was no date: a daily file named `17-10-2026` was
queued with none and refused later by the worker.

## What was measured first

On the laptop, 2026-11-11, on the real files. Each reader unpacked into a SHA-256 and nothing else;
every result equals the SHA-256 of the file that was packed.

| File | Packed | Unpacked | Reader | Time | Peak memory |
|---|---|---|---|---|---|
| The catalogue of 9 November, GSMA's own RAR 5 | 6,733,061 | 212,972,923 | SharpCompress 0.50.7 | 0.97-0.99 s | 71 MiB |
| The largest daily file uploaded to the laptop in October-November, as ZIP (deflate) | 217,031,631 | 570,159,191 | the runtime (`System.IO.Compression`) | 2.2 s | 33 MiB |
| the same ZIP | | | SharpCompress 0.50.7 | 3.9 s | 36-39 MiB |
| the same file as RAR 5 | 201,358,098 | 570,159,191 | SharpCompress 0.50.7 | 6.3-6.6 s | 70 MiB |

A daily file packs to 35-38% of its size; the catalogue to 3%. Over the department's VPN, at the
~130 KB/s measured on 2026-10-25, a 370 MB day would be about 17 minutes of upload where it is 45.

And what each reader noticed. ZIP and RAR: small synthetic archives with one byte changed, one trial
each. gzip: the tests of this change, run with the platform's own check taken out:

| | Noticed? |
|---|---|
| The runtime's ZIP reader, entry stored | **No.** Read back without complaint, content different |
| The runtime's ZIP reader, entry deflated | **No.** The same |
| The runtime's gzip reader, one byte changed | Yes |
| The runtime's gzip reader, file cut short | **No.** It stops quietly where the data stops |
| SharpCompress, RAR stored or packed | Yes: "file crc mismatch" |

The last "No" was already live: `--import-file` has unpacked `.gz` since 2026-10-25, with the
runtime's reader alone, so a day cut short in transfer would have been imported as a short day. The
five days carried that way were stored with their originals' SHA-256 (`02-department-vm.md` §9), so
none was; nothing in the platform would have said so.

## Decision

### 1. What arrives is told from its first bytes, never its name

A name is what the sender typed. A ZIP renamed `.csv` is unpacked; a CSV renamed `.zip` is stored as
the CSV it is. Four things are recognised and taken: a plain file, gzip, ZIP, and RAR (4 or 5).
Six more are recognised and refused by name - 7z, xz, bzip2, Zstandard, tar, and the older Office
formats - because "not unpacked here; send CSV, or a CSV packed as ZIP, RAR or GZ" is more use than
a column-contract error about the first line of a binary file.

### 2. What is stored is the file inside. The archive is not kept

The hash that says whether a file has been seen before is the hash of what is stored. So the same
catalogue sent as a CSV, as a `.gz` carried to the server in pieces, and as GSMA's `.rar` is one
file, and is imported once: the second and third arrivals answer "already imported". `--import-file`
has stored a `.gz` this way since it was written, for this reason.

Rejected: keeping the archive as the original. It would store a daily file at 35-38% of its size.
It would also make the same file two files depending on how it was sent, and every import and every
"Import again" would unpack it first - the catalogue is opened three times per import.

### 3. Unpacked in the request, not by the worker

At the times above, unpacking is short beside the upload it follows. Done in the request it needs
no second queue and no new kind of job, and the answer - which files were found, which wait for a
date - reaches the person still looking at the page. The upload routes already allow fifteen minutes
at the proxy.

A CSV and a gzip are streamed into the store as they arrive. An archive's list of files is at its
end, so it is copied first to `_incoming` on the store's own volume, unpacked from there, and the
copy removed; a resumable upload has already gathered on that volume and is unpacked where it lies.

Rejected: a job that unpacks. It is what a file of several gigabytes would need, and none arrives
that way: the dump is loaded from the shell.

### 4. ZIP by the runtime, RAR by SharpCompress

The runtime reads ZIP and gzip itself, and read the ZIP 1.75 times as fast as SharpCompress did. It
cannot read RAR. So SharpCompress 0.50.7 (MIT; no dependencies on .NET 10) is used for RAR and
nothing else: third-party code is on one path, the one that needs it. The version is pinned to the
one that was measured. `1.0.0` exists and is unlisted; `0.50.8` was a week old.

Two things learned from the library rather than assumed: in 0.50 `ExtractAllEntries` refuses an
archive that is not solid, so a solid archive is walked in order and any other is read file by
file; and given a path it looks beside the file for the other volumes of a split archive and reads
them too, so an archive is opened as a stream - what arrived is that one file.

### 5. What the readers do not check is checked here

- **ZIP.** Each file's CRC-32, as the archive records it, is compared with what came out, at the
  last byte and so before the store moves the file into place. A file that fails is never stored.
- **gzip.** A gzip ends with the CRC-32 and length of what it held. They are compared with what
  came out. A gzip made by joining several together reads as damaged by this rule, and is refused
  with words that say so: rather that than a short day.
- **RAR** checks itself, and a failed check is refused in the same words.

### 6. An archive is somebody else's program for what to write to this disk

- Only files named `.csv` are taken, under their own names and never their paths. What the packing
  program adds - `__MACOSX`, dot files, `Thumbs.db` - is left, and so is everything else, each with
  its reason in the answer.
- Nothing inside is unpacked a second time: a file that is itself a packing is left out.
- A password, or one volume of a split archive, is refused before anything is read.
- It is capped by what it declares and again by what comes out: 100 files, 10 GB a file (the
  store's own limit), 20 GB in all, and 2 GB must be left on the volume. An upload fills a disk at
  the speed of the network; an archive fills it at the speed of the disk.
- **It is taken whole or not at all.** Every file is unpacked and stored before any is registered.
  One that does not unpack sound refuses the archive and removes the others.

A refusal answers 400 with the reason in words the sender can act on, and nothing is kept.

### 7. A file's date is decided once, when it arrives, and kept on the file

`FileNameDate` reads the ways a name writes a date: year first (`2026-10-17`, `20261017`, the start
of a timestamp); a month by name in any order (`09Nov2026`, `November 9, 2026`); day and month as
numbers before the year (`17-10-2026`). Gregorian, 2000 to 2099. It reads a name only where it can
mean one date:

| The name | Is |
|---|---|
| `03-04-2026` | 3 April or 4 March: **ambiguous**, both offered, neither taken |
| `…_20251227_20260125`, the dump's range | **several** dates, not a day |
| `17-10-26` | not read: a two-digit year has three readings |
| `1405-08-19` | not read: Jalali dates are not (the owner's choice) |
| `DeviceDatabase.csv` | no date |

`FileDate` then settles it, in this order:

1. **The file's own name**, where it says one date.
2. **The name of the archive it is the only file of**, where the file's name says none, or several
   that the archive's date is one of. This is GSMA's delivery. An archive of several files dates none
   of them: its name is about the set.
3. **A person.** Otherwise the file is stored and registered and gets no job. It is listed on the
   Import Center at once, with what its name can mean: two buttons where it can mean two dates, a
   date field where it offers none. The server holds the answer to the same rule.

A date later than tomorrow is never taken, from a name or from a person. More ways of writing a date
are read now, so more things that are not a date can look like one; and a day that has not happened
would become "the latest delivery" on every dashboard. Tomorrow is allowed because the server keeps
UTC and the files are named in Tehran.

The date is recorded on the file (`imports.import_file.declared_date`, migration 026) with who gave
it - `name`, `archive` or `person` - and written on the import's timeline, so a date read from a name
can be seen, and seen to be wrong. A daily file's job still carries it as its business date, which is
what the queue orders by. **A snapshot's job does not, and must not:** a TAC job with a business date
would wait behind every earlier dataset that failed. Its version is labelled from the date on the
file.

Rejected: reading the catalogue's date from its contents. The newest `lastUpdatedDate` in the file
of 9 November is 6 November: near the dataset's date, and not it. It stays on the timeline as a check
a person can make.

### 8. What an upload answers

| | |
|---|---|
| 201 | One file, queued: its job, the date it was given, who gave it |
| 409 | One file, already imported: the import it duplicates (unchanged) |
| 202 | One file, stored and waiting for its date: why, and what may be chosen |
| 200 | An archive: what became of each file in it, and what was left out |
| 400 | Refused, with the reason. Nothing kept |

The preflight, which already answered "would you take this file" before the bytes are sent, now also
says what the name tells of the date - so "you will be asked for its date" is known at the start of
a forty-minute upload. It says nothing of an archive's: what an archive holds is not known until it
is here.

`--import-file` takes the same formats, through the same code, and `--date YYYY-MM-DD` for a file
whose name does not say. It exits 0 when everything is queued or already imported, 3 when the file
is refused, 4 when a file in it is stored and waits.

## What it costs, and what is not proven

- **Unpacking is inside the request.** The largest file so far takes 2.2 s as ZIP and 6.4 s as RAR.
  At those rates a 10 GB file would take about 40 s and 2 minutes, inside the proxy's fifteen.
- **A RAR's dictionary is memory.** Unpacking allocates the dictionary the archive was made with:
  32 MB by default, 71 MiB measured in all. An archive made with a 1 GB dictionary needs 1 GB, in a
  container allowed 1.5. The allocation is guarded and refused in words. **That path is not tested:**
  making such an archive needs several gigabytes of memory, and the one attempt on the laptop filled
  its disk. Reading the dictionary size from the archive's header before allocating is in the
  backlog.
- **RAR 4 is not tested.** WinRAR 7 no longer writes it, and none was to hand. The library reads it.
- **Storage is unchanged.** Originals are kept unpacked, as before: about 144 GB a year.
- **The archive's name is not kept as evidence beyond a column.** `archive_name` records it; the
  archive's own bytes are gone. They can be made again from the file that is kept.

## Consequences

- The catalogue can be uploaded as GSMA delivers it, on either machine, and is labelled by its date.
- A file whose name does not settle its date no longer fails in the worker minutes later; it never
  reaches the worker, and asks its question on the page where the upload was made.
- A damaged ZIP or a gzip cut short is refused at the door. Before, one would have imported.
- One new dependency, on one path.

## Where it is

`FileNameDate`, `FileDate`, `ImportIntake` (`Sqm.Application/DataImport`); `FileSignature`,
`ArchiveFile`, `ImportArrivals` (`Sqm.Infrastructure/DataImport`); `ImportEndpoints`,
`ResumableUploadEndpoints`; `ImportFile` in the worker; migration
`db/operational/migrations/026_file_dates_and_archives.sql`. The tests that hold it:
`FileNameDateTests`, `FileDateTests`, `ImportArrivalsTests` (real archives, the real store),
`ImportFormatsEndpointTests` (through HTTP), and the RAR fixtures and their README under
`Sqm.Integration.Tests/Fixtures/Archives`.
