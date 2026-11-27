# ADR-026: The Persian captcha

**Status:** Accepted, 2026-11-27.
**Decided by:** MCI's red line (FR-NSM-204/00 §2-3 row 3), and the owner: on 2026-11-09 that the red line comes
first; on 2026-11-26 that it is built now, and that the red lines document always takes precedence.

## Context

MCI's red line asks for a captcha on the sign-in page, in its own terms: **hidden at first and shown after three
failed logins (five elsewhere); Persian digits and letters in several Persian fonts; at least ten patterns;
resistant to OCR; with an on-screen keyboard.** Nothing may be loaded from outside MCI's data centres (§1-3
row 4), so no captcha service.

The product is English-only (CLAUDE.md). This is the one place Persian appears, decided 2026-11-09.

Sign-in already has its controls, and they stay: an account locks after five failures, doubling from one to
fifteen minutes (ADR-006); six attempts a minute per address; an Argon2id verification on every attempt,
real account or not. The captcha is added to them, not put in their place.

The owner also asked, on 2026-11-26, for every security mechanism of Ganjineh, another system of the estate. Its
captcha is a sum drawn as a picture and shown on every sign-in. Where it differs from the red line - always
shown, digits only - the red line is followed.

## Decision 1: asked for after failures, counted by address and by name

Every failed sign-in counts against two subjects: **the address it came from, and the name it tried.** The next
attempt needs a captcha once either has failed three times in fifteen minutes. Counting the name stops one
account being tried from many addresses; counting the address stops many names being tried from one.

- **A name counts whether or not an account has it,** so whether a captcha is asked for says nothing about which
  accounts exist (the same rule as the equal-cost unknown user of ADR-006).
- **Checked before the password.** A wrong or missing answer is refused without the password being looked at:
  it never counts towards locking the account, and never costs an Argon2 verification. An answer not sent at all
  counts as nothing - the page may not have known yet; a wrong one counts as a failure.
- **A good password forgets the name's failures, not the address's.** One person succeeding from a shared office
  address says nothing about another person's guesses from it.
- **Recovery is "elsewhere": five.** Five wrong codes from an address or for a name, and both asking for a code
  and using one need the captcha.
- **The page is told.** `GET auth/options` says whether this address needs it, so a reloaded page shows the
  picture at once; every refusal says whether the next attempt needs it (`captchaRequired`).
- An address the server does not know is not made a subject: Kestrel always knows it (behind nginx, forwarded
  headers are on, `deploy/compose`), and a shared "unknown" would let strangers ask the captcha of each other.

## Decision 2: what is drawn

- **Characters:** five, from 17 Persian letters and 6 Persian digits, at least one of each, never two digits
  side by side - inside right-to-left text a run of digits reads left to right, while the picture lays every
  character right to left. Left out: letters that a dot or a stroke lost under the noise would turn into
  another (ا ث پ چ خ ح ذ ز ش ض ظ غ ق گ), and the digits ۰ (a dot), ۱ (a stroke), ۵ (the shape of ه in several
  of the typefaces) and ۹ (the shape of و). A person must be able to read what an OCR cannot.
- **Several Persian typefaces:** each character in one of five - Vazirmatn, Sahel, Samim and Shabnam (SIL OFL
  1.1) and Tanha (public domain) - embedded in the API, their licences copied into its output, and each checked
  for every character when the API starts.
- **At least ten patterns:** twelve, each its own ground (gradient, dots, grid, hatching, waves, speckle), its own
  strokes across the characters (curves, arcs, strikes, a mix) in the characters' own inks, its own bend (a wave
  of the rows, a ripple of the columns, both, none), with or without faint decoy characters, and its own amount
  of turning and crowding.
- **Resistant to OCR:** characters turned, slanted, scaled, of mixed sizes and touching; crossed by strokes no
  colour sets apart; over a textured ground with decoys; bent. Measured below.
- **The answer** is read as typed on a Persian, Arabic or Latin keyboard: Arabic-Indic and Latin digits count
  as Persian, the Arabic ي and ك as ی and ک, and spaces, joiners and direction marks are ignored.
- **The on-screen keyboard** holds exactly the characters a captcha may contain; they come from the server with
  each picture, so the keyboard cannot offer one the captcha does not use. The answer box is set right to left
  in Vazirmatn, the one web font the page loads for it, restricted to Arabic script.

## Decision 3: drawn on the server, with SkiaSharp

Only a picture leaves the server; the characters never do.

| | |
|---|---|
| System.Drawing | Windows only since .NET 6: the API runs on Linux |
| ImageSharp | Its licence (Six Labors Split License) charges an organisation of MCI's size for commercial use |
| SVG | The characters would sit in the markup, or as glyph paths a script reads back |
| **SkiaSharp** | **MIT; maintained by Microsoft; its `NoDependencies` native library needs no fontconfig, so the read-only image needs no font directory** |

Characters are drawn one at a time in their isolated forms, so no shaping engine is needed. **Measured** on the
development laptop, Release build: **12.5 ms and 61.5 KiB per picture** on average over 300 pictures.

## Decision 4: kept in PostgreSQL, as hashes

The API may run as several replicas (ADR-007), so a picture issued by one must be checkable by another: the state
is in PostgreSQL, not in memory as Ganjineh keeps it.

- A **challenge** is found by the hash of its id and holds the hash of its answer bound to that id. It lives ten
  minutes and is answered once: it is deleted by the statement that checks it, right or wrong.
- A **failure** is counted against the hash of its address or name - never the name itself, because what people
  type into a username box is sometimes their password - and kept only for the window: each new failure removes
  those that stopped counting, and the session sweeper takes the rest.
- A new picture is limited to **twenty a minute per address**.
- A **wrong answer is audited** (`auth.captcha`), at most five per address in ten minutes: a flood of guesses is one
  line of evidence repeated.

## Rejected

- **Always shown,** as Ganjineh does: the red line says hidden at first.
- **A sum to work out:** the red line asks for letters and digits.
- **State in memory:** wrong with more than one replica.
- **A captcha service:** nothing from outside (§1-3 row 4).
- **An audio alternative:** not asked for by the red line, and Persian audio would need recorded characters of its
  own. A person who cannot see the picture is held at the third failure - in `docs/backlog.md`.

## Consequences

- A picture costs 12.5 ms of CPU and about 82 KB of JSON; a person needs one per attempt.
- A typeface without a character stops the API at start rather than drawing an empty box at someone's sign-in.
- No configuration is needed to deploy it. `Auth:Captcha:Enabled=false` exists for test copies only.
- Off-the-shelf OCR reads none of the pictures. A model trained on them could; the captcha raises the cost of
  guessing, and the lockout and the rate limits remain the controls that bound it.

## How it was proved

- **Tests:** `CaptchaTextTests` (30), `CaptchaRendererTests` (7), `CaptchaServiceTests` (14),
  `CaptchaEndpointTests` (6) in the backend; `CaptchaField.spec` (8) and six on the sign-in page and two on
  recovery in the frontend. The endpoint tests sign in over HTTP from addresses of their own: hidden at first,
  asked for after three, refused before the password (five wrong answers do not lock an account that five wrong
  passwords would), the page told, the audit written, recovery's five.
- **Faults put back, each caught:** in the backend, sign-in ignoring the verdict, a picture answered twice, an
  expired picture passing, asking after four, failures counted by address only, failures never lapsing, a good
  password forgetting the address too, Latin digits not read, a wrong answer not audited, recovery never asking,
  the page never told, two digits side by side; in the frontend, the field never shown, the answer not sent, no
  new picture after an attempt, the keyboard ignoring its limit, recovery dropping the answer.
- **Against OCR:** Tesseract 5.3.4 with its Persian model read **0 of 300 captchas** (5.4% of characters). The
  same characters drawn plainly - one typeface, no noise, no turn, no bend - it read 55 of 300 (18.3%), and 60.5%
  of their characters: it reads Persian, and it does not read these.
- **On Linux:** the renderer's tests ran in CI's own SDK container, and the native library's dependencies were
  resolved in the runtime image the API ships in.
