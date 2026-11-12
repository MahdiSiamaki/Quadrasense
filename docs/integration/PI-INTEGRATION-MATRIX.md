# PI integration matrix and gap analysis

**Status:** 2026-11-09. Read against the estate landscape's figure, version 6
(`fig1-system-landscape-v6`, the owner's), `docs/architecture/20-estate-landscape.md` and the code. The
API itself is in `PI-API-CATALOG.md`, its design in `PI-API-ARCHITECTURE.md` and ADR-024.

The landscape says of itself that its systems, names and stated flows are the owner's, and its protocol
detail a reconstruction. This page follows that: a flow drawn in v6 is a requirement; how it travels is
decided in ADR-024 or marked open.

## 1. What v6 draws at PI

v6 draws seven edges at Quadrasense (PI). Every other edge in the figure is between other systems.

| # | Edge in v6 | Flow type in the figure | Direction |
|---|---|---|---|
| E1 | GSMA TAC database → PI, "TAC → make · model · capability" | data & intelligence | in |
| E2 | BI and SNQM → PI, "usage, CDR & quality feeds" | data & intelligence | in |
| E3 | the signalling planes → PI, "signalling probes · SRI-SM · ATI · ULR · AIR" | signalling | in |
| E4 | PI → Lodestone, "device state" | data & intelligence | out |
| E5 | Lodestone → PI, "events" (CRM → Lodestone, "orders · CRM events") | data & intelligence | in |
| E6 | PI → Tessera, "facts to decide on" | data & intelligence | out |
| E7 | PI → the Skyconnect pair (unlabelled) | data & intelligence | out |

### What v6 changed from the landscape doc 20 was written from

- **Ganjineh (IRR), the identifier resource registry, holds the IMSI ranges** and hands Secureseed "IMSI
  portion per OF". The range allocator doc 20 gave PI (§4, container 6) is therefore no longer PI's. This
  answers doc 20's open question 6 for IMSI ranges; MSISDN ranges and EID pools are still unassigned.
- **Tessera reads PI directly** ("facts to decide on"), where the brief for this work said "normally
  through Lodestone". Both are served: the facts endpoint takes any client holding `facts.read`, so
  registering Tessera directly or letting Lodestone carry it is a registration choice (owner, 2026-11-09).
- **CRM holds the SIM output files** Secureseed uploads ("SIM key output files, uploaded automatically").
  Nothing of that reaches PI.
- **The SIM triplet** reaches Skyconnect from Lodestone. PI's own edge to the Skyconnect pair (E7) carries
  no label.

## 2. The matrix

Authentication for every API below is OAuth 2.0 client credentials issued by PI (ADR-024): a registered
client, a 15-minute bearer token, scopes. "Lodestone or Tessera" means the client registered for that
system.

| Source system | Target system | Data | Direction | Integration type | API / event | Authentication | Implementation status |
|---|---|---|---|---|---|---|---|
| Lodestone | PI | A number, SIM (IMSI or ICCID) or handset (an IMEI of 14, 15 or 16 digits): its bindings, derived ICCIDs, and each handset's model, type and capabilities | Lodestone pulls from PI | REST, request/response | `POST /api/integration/v1/inventory/lookup` | OAuth 2.0 client credentials, `inventory.read` (+ `identifiers.reveal` for full identifiers) | **Implemented** |
| Lodestone | PI | Up to 50 identifiers at once | Lodestone pulls | REST | `POST /inventory/lookup/batch` | `inventory.read` | **Implemented** |
| Lodestone | PI | Everything connected to an identifier, with counts | Lodestone pulls | REST | `POST /inventory/relationships` | `inventory.read` | **Implemented** |
| Lodestone | PI | Binding history (periods and events) and what CRM declared | Lodestone pulls | REST | `POST /inventory/history` | `inventory.read` | **Implemented** |
| Lodestone | PI | Device profile: TAC → make, model, type, OS, capabilities (E1's result, carried out) | Lodestone pulls | REST | `GET /devices/models/{tac}` | `inventory.read` | **Implemented** |
| PI | Lodestone | "device state": a day of the binding feed imported (E4) | PI publishes, Lodestone pulls | Event feed (transactional outbox, cursor) | `GET /events` → `inventory.day-imported` | `events.read` | **Implemented** |
| PI | Lodestone | The day's binding adds and removes, a page at a time (E4) | Lodestone pulls | REST, keyset-paged | `POST /inventory/changes` | `inventory.sync` | **Implemented** |
| PI | Lodestone | A GSMA catalogue version activated: models may have changed (E1 → E4) | PI publishes, Lodestone pulls | Event feed | `GET /events` → `catalogue.version-activated` | `events.read` | **Implemented** |
| CRM via Lodestone | PI | Declared SIM swap, activation, deactivation (E5) | Lodestone pushes to PI | REST, idempotent by event id | `POST /events/declared` | `events.write` | **Implemented**: stored and shown; not yet used by the risk rules |
| PI | Tessera (direct, or via Lodestone) | Device and SIM facts: model and capabilities, network age, binding counts, risk measures and evidence level, declared events (E6) | Tessera pulls | REST | `POST /inventory/facts` | `facts.read` | **Implemented** |
| PI | BI, SNQM, Lodestone | Data quality: feed quality per day, quality signals, feed freshness | Pulled | REST | `GET /quality/feed`, `GET /quality/signals`, `GET /status` | `quality.read` (status: any client) | **Implemented** |
| Administrator | PI | Register a system, its scopes; rotate its secret; deactivate | A person, in PI | REST, behind a session | `/api/v1/integration/clients` | Session + `integration.manage` | **Implemented** (API; no settings page yet) |
| GSMA TAC database | PI | TAC → make, model, capability (E1) | In | Periodic sync in the landscape | none | - | **Partial**: a versioned upload by hand (ADR-009); no sync: needs GSMA access the estate has not given PI |
| SNQM | PI | Today's daily binding dumps | In | Files, imported | `/api/v1/imports/SQM/upload` (UI) | Session | **Built** before this work; the landscape does not draw it |
| BI, SNQM | PI | CDR and usage aggregates; per-model KPIs and QoE (E2) | In | Batch feed in the landscape | none | - | **Not built**: no format, schedule or sample exists; building an intake would invent BI's and SNQM's data model |
| Signalling planes | PI | IMEI-SV, visited network, reachability (E3: ULR, AIR, SRI-SM, ATI) | In | Probe / tap | none | - | **Not built**: needs a tap on S6a and MAP, a streaming intake, and a decision past the overview's "no streaming" non-goal |
| PI | Skyconnect pair | Campaign targets: SIM triplet, keyset references, contract data (E7; doc 20) | Out | Unlabelled in v6 | none beyond `POST /inventory/lookup/batch` | - | **Not built as its own interface**: PI holds no keyset references and no contract data; the triplet (MSISDN, IMSI, derived ICCID) is in every lookup |
| Partners, MVNOs | PI | Any of the above | Through Lodestone only | - | - | Lodestone's OAuth2/mTLS and tenancy | **By design**: PI registers Lodestone, never a partner |
| Ganjineh | Secureseed, Velafabric | IMSI ranges, IMSI portion per OF | - | - | - | - | **Not PI's** in v6 |
| Provisioning | HSS | Subscriber create, Ki/OPc load | - | - | - | - | **Not PI's**: PI never writes to the network and holds no key values |

## 3. Gap analysis

What the landscape asks of PI, against what PI's API offered before this work and offers after it.

| Requirement (landscape v6, doc 20) | Before this work | After | Gap left, and what closes it |
|---|---|---|---|
| A query API for Lodestone (doc 20 §4, container 8) | None: every route behind a person's session, for PI's own UI | Lookup, batch, relationships, history, device model, under `/api/integration/v1` | None for the data PI holds |
| Machine-to-machine authentication | None | OAuth 2.0 client credentials issued by PI; scopes; hashed secrets and tokens | mTLS at the ingress, if MCI or Lodestone asks for it: a platform setting, not code |
| SIM-lifecycle and device-state events to Lodestone (E4) | None | Outbox + pulled feed; day-imported and catalogue events; the day's changes by page | Per-binding events pushed as they happen: the feed arrives once a day, so they would be the same day's changes, later |
| CRM's declared events (E5, container 3) | None: SIM changes only *observed* (ADR-014) | Intake, idempotent; shown in history and facts | The risk rules could discount a declared swap; a separate decision (backlog) |
| Facts for Tessera (E6) | None | `POST /inventory/facts` | Contract facts (plan, SIM type sold): CRM's, not PI's |
| eUICC inventory: EID, profile state | None | None | **No feed carries them.** An RSP or Fusionflow feed, and who owns profile stock (doc 20, question 4) |
| ICCID | Derived from the IMSI by the operator's numbering, in the Explorer | Derived, in every answer, labelled `derived`; accepted as an input and resolved | A delivered ICCID: from Secureseed's output files or CRM; neither reaches PI |
| SIM stock, lot, vendor | None | None | No feed; doc 20 question 5 |
| Range allocator for Secureseed (doc 20 container 6) | None | None | **Moved to Ganjineh in v6**: not PI's |
| TAC sync (E1, container 4) | Manual upload | Unchanged; catalogue activations now published as events | GSMA access for PI, and a decision to automate activation (today a person activates) |
| BI and SNQM feeds (E2) | None | None | Each system's format and schedule |
| Signalling taps (E3, containers 1 and 2) | None | None | A tap, a streaming intake, and the decision to lift "no streaming" |
| Campaign targets for Skyconnect (E7) | An approved CSV carried by a person (`19-network-planning.md` §4) | Batch lookup gives the triplet for up to 50 identifiers | Keyset references and contract data: no feed; a list-sized target resolver would be a new, audited export |
| Data quality for BI and SNQM | UI only | `quality.read` | None |
| Inventory writes from other systems | None | None, by design | PI's inventory is what the feeds say; a write API would let a caller contradict the evidence. A correction belongs at the source |
| Partners and MVNOs | None | Through Lodestone only | Per-tenant data partitions in PI would need range ownership PI does not hold |

## 4. Open decisions

| # | Decision | Why it is open | Recommended |
|---|---|---|---|
| D1 | Use declared swaps in the risk rules | A declared swap explains an observed SIM change; discounting it changes what Risk lists | A separate change, after Lodestone delivers real events and their volume is known |
| D2 | Who owns eSIM profile state and stock | Doc 20, question 4; PI has no eUICC feed | The owner's |
| D3 | Lodestone's event backbone | Doc 20, question 11; PI's feed is pull, which works with any backbone Lodestone chooses | Keep pull until Lodestone names a broker and MCI allows egress |
| D4 | mTLS between Lodestone and PI | MCI's ingress and certificates (red lines §2-1 row 8) | Ask with the stage 2 network request |
| D5 | A settings page for integration clients | Today an administrator uses the API | **Decided 2026-11-10 (owner): build it now. Built 2026-11-12:** Settings > API clients, for `integration.manage` - register, change, deactivate, issue a new secret (shown once, after asking). The API stays as it was |
| D6 | Expose the integration OpenAPI document in production | It is served in Development only, as the UI's is | **Decided 2026-11-10 (owner): readable in the product by anyone signed in.** `GET /api/v1/integration/openapi` serves the document the running server generates, behind a session and no permission; the anonymous `/openapi` routes stay Development's (ADR-024, amendments) |
