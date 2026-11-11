# ADR-024: PI's integration API - how the estate's systems reach Primary Inventory

- **Status:** Accepted
- **Date:** 2026-11-09
- **Decided by:** the product owner asked on 2026-11-09 for production-ready integration APIs so that PI exchanges
  information with the other systems of the estate as the landscape's figure v6 draws it, starting with the
  inventory reads and PI ↔ Lodestone. Four choices were the owner's, asked the same day: PI issues OAuth 2.0
  client credentials; Lodestone pulls PI's events; the facts endpoint serves any client holding the scope; CRM's
  declared events are stored now.

## Context

Doc 20 (`20-estate-landscape.md` §4) listed PI's machine interfaces as *not built*: every route sat behind a
person's session, for PI's own UI. Figure v6 draws seven edges at PI (`docs/integration/PI-INTEGRATION-MATRIX.md`
§1): the GSMA catalogue, BI and SNQM, and the signalling planes in; "device state" to Lodestone and "events" from
it; "facts to decide on" to Tessera; an unlabelled edge to the Skyconnect pair. v6 also moved the IMSI ranges to a
new system, Ganjineh (IRR), taking the range allocator away from PI.

PI must answer what it observed and nothing it does not hold: it never writes to the network, never sends to a
card, never decides policy, and holds key references at most, never key values (CLAUDE.md).

## Decision 1: a separate API, for systems only

`/api/integration/v1`, beside the UI's `/api/v1`, versioned in the path, published in its own OpenAPI document. Its
routes accept a system's bearer token and nothing else; the UI's accept a person's session and nothing else. The
bearer scheme is never the default, and every integration route names an integration policy -
`EndpointAuthorizationTests` fails on a route that names a permission or none.

## Decision 2: OAuth 2.0 client credentials, issued by PI (owner, 2026-11-09)

An administrator (`integration.manage`, a new permission given to Administrator) registers each system with the
scopes it needs; the system trades its id and secret for a 15-minute opaque bearer token (RFC 6749 §4.4).
Secrets and tokens are 32 random bytes, stored only as SHA-256 (red lines §2-3 row 8), looked up per request as
sessions are. Deactivating a client or rotating its secret ends its tokens in the same transaction.

## Decision 3: scopes, and the UI's masking rule

Seven scopes (`inventory.read`, `inventory.sync`, `facts.read`, `events.read`, `events.write`, `quality.read`,
`identifiers.reveal`), each route naming one. Kept apart from permissions so that neither can satisfy the other's
policy. `identifiers.reveal` is the UI's `identifier.reveal` for a system: without it every identifier in an answer
is masked but the one the client sent.

## Decision 4: identifiers in bodies; the ICCID resolved; no EID

Every read that names a number, SIM or handset is a `POST` (red lines §2-3 row 7), the sync cursor included. An
ICCID is accepted and resolved to its IMSI by the operator's numbering rule - the only ICCID PI has is that
derivation, labelled `derived` in every answer. An EID is refused: no feed PI receives carries eUICC data.

## Decision 5: the UI's reads, not new ones

Lookup, batch and history read the binding history through `TimelineBuilder`, as the Explorer's timeline;
relationships the relationship explorer's store; device models the device page's; facts add the risk page's own
entity assessment, extracted into `RiskEndpoints.AssessAsync` so that both share it. The only new read is a day's
changes: one partition of `binding_event`, keyset-paged on its sort key.

## Decision 6: events out through a transactional outbox, pulled (owner, 2026-11-09)

PI writes its events to `integration.outbox_event` and Lodestone reads them with a cursor (`GET /events?after=`).
A day of the binding feed is published in the transaction that completes its import; a GSMA catalogue activation
once both stores have switched. Writers take one advisory lock before inserting, so sequence order is commit order.
Events are thin - what happened, not the data - and the detail is read through the API with the reader's scopes.
At least once; kept 30 days.

## Decision 7: declared events in, idempotent, not yet acted on (owner, 2026-11-09)

Lodestone delivers CRM's SIM swaps, activations and deactivations (`POST /events/declared`). Stored once per
sender id, with a content hash that tells a retry (`duplicate`) from a reused id (`conflict`); append-only for the
application role. Shown in history and facts as **declared**, beside what was **observed**. The risk rules do not
use them yet: that changes what Risk lists, and is a decision of its own.

## Decision 8: facts are observations (owner, 2026-11-09: any client with `facts.read`)

`POST /inventory/facts` gives Tessera - directly, as v6 draws it, or through Lodestone - what its rules are written
against: binding counts, network age, the handsets in active bindings with their models and capabilities, the risk
measures and the evidence level they reach, and the declared events. Never eligible, blocked or grey-listed: those
are Tessera's.

## Decision 9: limits after authentication

600 requests a minute per client, as an endpoint filter (the rate-limiting middleware runs before the client is
known, and one address - Lodestone's - carries every partner); 30 token requests a minute per address. A batch is
50 identifiers answered one at a time, because the laptop's ClickHouse runs one heavy statement at a time
(`dedicated-hardware.md`).

## Rejected

| Option | Why not |
|---|---|
| Tokens from an external identity provider (JWT) | No issuer or keys exist for PI to trust; it could only be built against configuration and tested with self-made tokens. Revisit if MCI's platform supplies one |
| Static API keys | Long-lived credentials on every call, no expiry, no scope change without re-issuing |
| JWT access tokens issued by PI | Not revocable before expiry without a lookup anyway; an opaque token looked up per request is revocable at once and reveals nothing if leaked |
| Pushing events to Lodestone | Needs an endpoint Lodestone has not named and an egress request (red lines §1-4 row 4); a pulled feed survives a reader that was down |
| A message broker | Doc 20 open question 11; new infrastructure no requirement forces today |
| An event per binding change | About eight million a day; the day's changes are already ordered in the event log and served by page |
| Exposing PI to partners | Lodestone owns tenancy, quotas and contracts; PI registers Lodestone, never a partner |
| Inventory writes from other systems | PI's inventory is what the feeds say; a correction belongs at the source |
| An EID or profile-state API | No data: no feed carries eUICC information |
| A range allocator | Ganjineh's in v6 |
| BI and SNQM intakes, TAC sync, signalling taps | No format, access or tap exists; an intake now would invent another system's data model |

## Consequences

- PI has a machine interface for the first time, and a new permission and schema to administer. No settings page
  yet: an administrator registers a client through the API (matrix D5).
- Lookups depend on the binding history being complete; until it is, they answer 503 rather than an incomplete
  answer.
- The OpenAPI document is generated; the committed copy is held to it by a test.
- Declared events accumulate without being used by the risk rules until that decision is taken.

## How it was proved

- `IntegrationApiTests` (35 cases, the real host with in-memory stores): tokens - grant, narrower scope, wrong
  secret, unknown and inactive client, unknown scope, wrong grant, not a form, the per-address limit; bearer - none,
  bad, expired, missing scope (403, audited), a session on an integration route and a token on a UI route; masking
  with and without `identifiers.reveal`; ICCID resolution; EID and underivable ICCID refused with the reason; no
  identifier in the audit log; batch; 503 while the history is built; facts; device model; changes paged by cursor
  and a tampered cursor refused; declared events accepted, duplicate and conflict; an invalid event refused by field
  with its neighbours kept; declared events in the history of the number and of either SIM; the feed resumed after a
  cursor; registering, listing without the secret, deactivating; no contract able to carry a key field; the OpenAPI
  document holding only integration routes.
- `IntegrationStoreTests` (6, the real PostgreSQL, as the application role): grant and resolve, only hashes stored,
  rotation and deactivation ending tokens, expiry and sweep, a declared event once per id and the application unable
  to delete one, the feed in sequence and an event existing only if its transaction committed.
- `IntegrationRealDataTests` (2, the real ClickHouse): a day's changes paged with no gap or repeat; a number from the
  data found through the binding history. `IntegrationOpenApiTests`: the committed document is the generated one.
- **Eight bugs put back, each caught:** every identifier revealed; a session satisfying an integration policy; any
  token passing any scope; an expired token resolving; a reused event id taken for a retry; an ICCID looked up as
  itself; no per-client limit; an identifier written into the audit log.
- **End to end on the laptop's stack and the real data**, through the running API with a temporary client: token
  101 ms; a day's changes 115-244 ms per 1,000 rows and 167 ms for 5,000, pages without overlap; lookups by number,
  IMSI, ICCID and IMEI 0.4-1.1 s, each finding the changed binding; history 321 ms; relationships 309 ms; facts 367-473
  ms with the risk level and network age; device model 52 ms; a batch of 20 in 4.7 s; quality signals 5.1 s (the Data
  quality page's own read); a declared swap accepted, then a duplicate, then shown in the history. The audit log held
  every call and no identifier. The client was deleted afterwards.
- **Not proved:** a real Lodestone or Tessera calling PI; a real CRM event; an `inventory.day-imported` event from a
  real import (the outbox write is in the completion transaction, proved for the outbox itself, not yet by a day
  imported after this change).

## Amendments, 2026-11-10

The owner asked whether the API covers everything a system could want - "can a system give a number and get its
devices, and the same for an IMSI or an IMEI?" - and for a place in the product to read the API. The coverage was
read from the code, and three choices were the owner's the same day: close the two small gaps below and record
the rest; let anyone signed in read the guide; build the clients page beside it.

- **An identifier nobody holds answered 500, where the catalogue says `"found": false`.** `TimelineBuilder` took
  the earliest lifetime with `MinBy` over (binding, lifetime) tuples, which throws on an empty sequence of a
  value type. Lookup, history and facts shared it with the Explorer's timeline route, which had failed the same
  way since 2026-10-01 and never showed its own words for the case. Every stub in the tests returned bindings,
  and the first end-to-end run looked up only identifiers read from the data: "not proved" should have listed an
  unknown identifier, and did not. Found by looking one up against the real data.
- **An IMEI is accepted as other systems hold it.** The feed carries fourteen digits. Fifteen with the Luhn check
  digit (as printed, as a CRM keeps it), fifteen with the spare zero a handset transmits in its place, and sixteen
  as an IMEISV (3GPP TS 23.003 §6.2) are reduced to them, and the answer names the handset in fourteen. A
  fifteenth digit that is neither is refused as mistyped, not looked up as some other handset. Fifteen digits
  with no `kind` stay an IMSI; sixteen are nothing else.
- **Each binding of a lookup says what its handset is**: `deviceType`, `operatingSystem` and `capabilities`
  (`lte`, `fiveG`, `esim`; null where the catalogue does not say, and for a TAC it does not list). "The devices
  of this number" had been a lookup and then a device-model call per TAC. One read of the catalogue per lookup,
  and one per model in a batch. Decision 5 still holds for the bindings; this is the second read that is the
  integration API's own, and it is a primary-key read of the catalogue, not of the network.
- **The contract is readable in the product** (matrix D6). `GET /api/v1/integration/openapi` serves the document
  the running server generates, behind a session and no permission: it holds no data and no secret, and a
  developer of another system reads it from an account that can do nothing else. The API guide under Settings
  reads it; a page for registering clients (matrix D5) is built with it.
- **What the API still does not offer a system**, left until one asks (`docs/backlog.md`): population statistics
  (the dashboard's figures), catalogue search by name, planning and risk lists, newly arrived models, and
  delivery of the daily file by a machine.

### How this was proved

- Tests: `ImeiFormTests` (16), `TimelineBuilderTests` (+3) and the Explorer's `TimelineEndpointTests` (+1) for an
  empty timeline, `IntegrationApiTests` (+10: the four forms of an IMEI, fifteen digits as an IMSI, a mistyped
  check digit, a binding's device facts and none for an unlisted model, one catalogue read per model in a batch,
  an identifier nobody holds across lookup, history, facts and batch, and the guide's document for anyone signed
  in and nobody else). The empty-timeline test was watched failing with "Sequence contains no elements" before
  the fix.
- **Five bugs put back, each caught:** any fifteenth digit accepted; a batch forgetting the models it read; the
  guide's document open to anyone; a binding saying nothing of its handset; sixteen digits not read as an IMEISV.
- **End to end on the laptop's stack and the real data**, 2026-11-10, with a temporary client: 67 bindings of 40
  numbers over 53 models, every one carrying its facts, and 25 of 25 models agreeing with the device model route;
  an IMEI in each form answered as the fourteen-digit lookup; a wrong check digit 400; a number PI does not hold
  200 and `found: false`. From ClickHouse's query log over 135 lookups: the catalogue read 6.4 ms on average (p90
  8 ms, 24,516 rows, 4 MiB) beside the timeline read's 173 ms. Forty lookups by number: median 194 ms, p90 245
  ms. A batch of 50 over 77 models: 9.8 s, 195 ms an identifier, against 235 ms an identifier the day before
  without the catalogue read.
- Each commit was built and tested on its own in a clean worktree.
