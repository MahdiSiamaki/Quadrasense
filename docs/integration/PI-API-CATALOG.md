# PI integration API: catalogue

**Version:** 1.0, under `/api/integration/v1`. **Status:** 2026-11-09. Design: `PI-API-ARCHITECTURE.md` and
ADR-024. Which system uses what: `PI-INTEGRATION-MATRIX.md`. The machine-readable contract is
`openapi/pi-integration-v1.json`, held to the code by `IntegrationOpenApiTests`.

Every identifier in this page is made up. The repository is public, and the real dataset never appears in
it.

## Conventions

| | |
|---|---|
| Base path | `/api/integration/v1`. A breaking change is a `v2` beside it, never a change to `v1` |
| Authentication | `Authorization: Bearer <token>`, from the token endpoint. A session cookie opens nothing here, and a token opens nothing of the UI's API |
| Identifiers | In request **bodies**, never URLs (MCI red lines, §2-3 row 7): every read that names a number, SIM or handset is a `POST` |
| Identifier kinds | `msisdn` (10 digits), `imsi` (15), `iccid` (19, resolved to its IMSI), `imei` (14). Read from the length when `kind` is left out. **No EID**: PI receives no eUICC data |
| Masking | Without `identifiers.reveal`, every IMSI, ICCID, IMEI and number in an answer is masked, but the identifier the client sent. Each answer says `"identifiers": "full"` or `"masked"` |
| ICCID | Derived from the IMSI by the operator's numbering, and labelled `"iccidSource": "derived"`. No feed PI receives carries one |
| Dates | `yyyy-MM-dd`; moments ISO 8601 in UTC |
| Errors | RFC 7807 problem documents (`application/problem+json`), field errors under `errors`; the token endpoint answers RFC 6749 §5.2 errors instead. Every answer carries `X-Correlation-Id` |
| Limits | 600 requests a minute per client (`429` with `Retry-After`); 30 token requests a minute per address |
| Idempotency | Reads are safe to repeat. Declared events are idempotent by `eventId`. The event feed is at-least-once: de-duplicate on `id` |

### Scopes

| Scope | Allows |
|---|---|
| `inventory.read` | Look up numbers, SIMs (IMSI or ICCID) and handsets: bindings, relationships, history, device models |
| `inventory.sync` | Read each day's binding changes, to keep a copy in step |
| `facts.read` | Read the device and SIM facts a policy decision is written against |
| `events.read` | Read PI's event feed |
| `events.write` | Deliver declared events - SIM swaps, activations, deactivations - to PI |
| `quality.read` | Read data-quality figures and the state of the feeds |
| `identifiers.reveal` | Receive IMSIs, ICCIDs and IMEIs in full; without it they are masked |

## Endpoints

| Method and path | Scope | What it answers |
|---|---|---|
| `POST /oauth/token` | none (client credentials) | A 15-minute bearer token |
| `GET /status` | any client | The client, its scopes, how fresh PI's data is |
| `POST /inventory/lookup` | `inventory.read` | One identifier's bindings, active and ended |
| `POST /inventory/lookup/batch` | `inventory.read` | Up to 50 lookups, each on its own |
| `POST /inventory/relationships` | `inventory.read` | Every number, SIM and handset connected to one identifier |
| `POST /inventory/history` | `inventory.read` | Every binding over time, and what CRM declared |
| `GET /devices/models/{tac}` | `inventory.read` | What a TAC is: make, model, type, capabilities |
| `POST /inventory/facts` | `facts.read` | Device and SIM facts for a policy decision |
| `POST /inventory/changes` | `inventory.sync` | One day's binding adds and removes, by page |
| `GET /events` | `events.read` | PI's events after a cursor |
| `POST /events/declared` | `events.write` | CRM's declared events, delivered by Lodestone |
| `GET /quality/feed` | `quality.read` | Each daily file against the ordinary days |
| `GET /quality/signals` | `quality.read` | Data-quality signals over the current state |

The administrators' routes are part of the UI's API, behind a session and `integration.manage`:
`GET/POST /api/v1/integration/clients`, `PUT /api/v1/integration/clients/{id}`,
`POST /api/v1/integration/clients/{id}/secret`.

---

### `POST /oauth/token`

The OAuth 2.0 client credentials grant (RFC 6749 §4.4). HTTP Basic, or `client_id` and `client_secret` in
the form. `scope` is optional: all the client holds when left out, a subset otherwise.

```http
POST /api/integration/v1/oauth/token
Authorization: Basic bG9kZXN0b25lOi4uLg==
Content-Type: application/x-www-form-urlencoded

grant_type=client_credentials&scope=inventory.read%20events.read
```

```json
{ "access_token": "Xq3…43 characters…", "token_type": "Bearer", "expires_in": 900, "scope": "events.read inventory.read" }
```

| Status | `error` | When |
|---|---|---|
| 400 | `invalid_request` | Not a form |
| 400 | `unsupported_grant_type` | Anything but `client_credentials` |
| 400 | `invalid_scope` | A scope this API does not know, or the client does not hold |
| 401 | `invalid_client` | Unknown client, wrong secret or a deactivated client: not told apart |
| 429 | | Over 30 requests a minute from the address |

### `GET /status`

```json
{
  "apiVersion": "1.0",
  "client": "lodestone",
  "scopes": ["events.read", "inventory.read"],
  "dataThrough": "2026-10-31",
  "history": "Ready",
  "sources": [
    { "source": "SQM", "latestBusinessDate": "2026-10-31", "daysBehind": 9, "missingDays": 11 },
    { "source": "TAC", "latestBusinessDate": "2026-10-04", "daysBehind": 36, "missingDays": 0 }
  ]
}
```

`history` is `Ready`, `Building` or `NotDeployed`. Lookups, history and facts answer `503` unless it is
`Ready`: an answer from a history still being built would miss days without saying so.

### `POST /inventory/lookup`

```json
{ "identifier": "9120000001" }
```

`kind` may be given (`"msisdn"`, `"imsi"`, `"iccid"`, `"imei"`); otherwise it is read from the length.
Without `identifiers.reveal`:

```json
{
  "kind": "msisdn",
  "identifier": "9120000001",
  "resolvedImsi": null,
  "found": true,
  "summary": {
    "bindings": 2, "activeBindings": 1, "numbers": 1, "sims": 2, "handsets": 2,
    "firstSeen": "2025-12-27", "firstSeenIsDumpWindow": true, "lastChange": "2026-09-01"
  },
  "bindings": [
    {
      "msisdn": "9120000001",
      "imsi": "43211******0001",
      "iccid": "899811*********0001",
      "iccidSource": "derived",
      "imei": "35085748******",
      "tac": "35085748",
      "brand": "Samsung",
      "model": "Galaxy A32",
      "active": true,
      "firstSeen": "2026-09-01",
      "firstSeenIsDumpWindow": false,
      "lastChange": "2026-09-01"
    }
  ],
  "truncated": false,
  "identifiers": "masked",
  "dataThrough": "2026-10-31"
}
```

With `identifiers.reveal` the same answer carries `"imsi": "432110000000001"`,
`"iccid": "8998110000000000001"` and `"imei": "35085748000001"`.

**An ICCID** is resolved by the operator's numbering and answered as its IMSI:

```json
{ "identifier": "8998110000000000001" }
```

```json
{ "kind": "iccid", "identifier": "8998110000000000001", "resolvedImsi": "432110000000001", "found": true, "…": "…" }
```

An ICCID the numbering does not produce is refused with the reason, not reported as not found:

```json
{
  "title": "One or more validation errors occurred.",
  "status": 400,
  "errors": { "identifier": ["This ICCID is not one the operator's numbering derives from an IMSI, so PI cannot resolve it: no feed PI receives carries ICCIDs."] }
}
```

### `POST /inventory/lookup/batch`

```json
{ "identifiers": [ { "identifier": "9120000001" }, { "identifier": "x" }, { "identifier": "35085748000001", "kind": "imei" } ] }
```

```json
{
  "items": [
    { "index": 0, "outcome": "found", "problem": null, "result": { "…": "a lookup" } },
    { "index": 1, "outcome": "invalid", "problem": "A phone number (10 digits), an IMSI (15), an ICCID (19) or an IMEI (14). PI holds no EID: no feed it receives carries eUICC data.", "result": null },
    { "index": 2, "outcome": "found", "problem": null, "result": { "…": "a lookup" } }
  ]
}
```

Between 1 and 50 identifiers; answered one after another, never in parallel (laptop: one heavy statement
at a time, `docs/operations/dedicated-hardware.md`).

### `POST /inventory/relationships`

The relationship explorer's graph: the numbers, SIMs and handsets connected to the identifier, with binding
counts, and handsets that share subscribers (dual-SIM pairs).

```json
{ "identifier": "35085748000001" }
```

```json
{
  "centre": "35085748000001", "kind": "Imei", "found": true,
  "subscribers": [ { "value": "912*****01", "kind": "Msisdn", "bindings": 1, "activeBindings": 1, "lastConfirmed": null } ],
  "sims": [ { "value": "43211******0001", "kind": "Imsi", "bindings": 1, "activeBindings": 1, "lastConfirmed": null } ],
  "handsets": [ { "value": "35085748000001", "kind": "Imei", "bindings": 1, "activeBindings": 1, "tac": "35085748", "brand": "Samsung", "marketingName": "Galaxy A32", "deviceType": "Smartphone" } ],
  "paired": [], "withheld": [], "truncated": false, "elapsedMs": 41, "rowsExamined": 8192,
  "identifiers": "Masked"
}
```

### `POST /inventory/history`

The binding history, as the Explorer's timeline draws it, and what CRM declared about the number or the SIM.

```json
{ "identifier": "9120000001" }
```

```json
{
  "kind": "msisdn",
  "identifier": "9120000001",
  "resolvedImsi": null,
  "timeline": {
    "kind": "msisdn", "identifier": "9120000001",
    "summary": { "firstSeen": "2025-12-27", "firstSeenIsDumpWindow": true, "lastChange": "2026-09-01", "bindings": 2, "activeBindings": 1, "numbers": 1, "sims": 2, "handsets": 2, "redundantAdds": 0, "orphanRemoves": 0, "stateDisagreements": 0 },
    "bindings": [
      { "msisdn": "9120000001", "imsi": "43211******0001", "imei": "35085748******", "active": true,
        "periods": [ { "start": "2026-09-01", "startsInDump": false, "end": null } ],
        "events": [ { "date": "2026-09-01", "change": "add", "effect": "…" } ], "…": "…" }
    ],
    "dataThrough": "2026-10-31", "…": "…"
  },
  "declared": [
    {
      "eventId": "crm-000123", "source": "crm", "type": "sim-swap",
      "occurredAt": "2026-08-31T05:45:00Z", "receivedAt": "2026-08-31T05:45:02Z",
      "msisdn": "9120000001", "imsi": "43211******0001", "iccid": null,
      "previousImsi": "43211******0002", "previousIccid": null,
      "reference": "ORD-000123", "deliveredBy": "lodestone"
    }
  ]
}
```

A handset has no declared events: CRM declares facts about numbers and SIMs.

### `GET /devices/models/{tac}`

```json
{
  "tac": "35085748", "knownToGsma": true, "manufacturer": "Samsung Korea", "brand": "Samsung", "model": "SM-A325F",
  "marketingName": "Galaxy A32", "deviceType": "Smartphone", "operatingSystem": "Android",
  "capabilities": {
    "lte": true, "fiveG": false, "esim": false, "imsEmergency": true, "bands": "…",
    "simSlots": "2", "imeiQuantity": "2", "nfc": "Y", "wlan": "Y", "bluetooth": "Y"
  },
  "allocationDate": "2021-02-01", "catalogueVersionId": 7
}
```

`404` when neither the active GSMA catalogue nor the network knows the TAC. **No VoLTE flag**: the GSMA
catalogue does not hold one (`19-network-planning.md` §1).

### `POST /inventory/facts`

What a policy is written against, for Tessera: what PI observed, never a decision.

```json
{ "identifier": "35085748000001" }
```

```json
{
  "kind": "imei", "identifier": "35085748000001", "resolvedImsi": null, "found": true,
  "summary": { "bindings": 1, "activeBindings": 1, "numbers": 1, "sims": 1, "handsets": 1, "firstSeen": "2026-09-01", "firstSeenIsDumpWindow": false, "lastChange": "2026-09-01" },
  "networkAgeDays": 60,
  "devices": [ { "imei": "35085748000001", "model": { "tac": "35085748", "marketingName": "Galaxy A32", "capabilities": { "lte": true, "fiveG": false, "esim": false, "…": "…" }, "…": "…" } } ],
  "risk": {
    "kind": "imei", "family": "Imei", "stored": true, "level": "Observation", "assessable": true,
    "reasons": [], "values": { "…": "the measures" }, "ruleSetVersion": "…", "asOf": "2026-10-31", "pattern": "…", "linked": []
  },
  "riskUnavailable": null,
  "declared": [],
  "identifiers": "masked",
  "dataThrough": "2026-10-31"
}
```

`risk` is the risk page's own assessment: measures and the **evidence level** they reach (Observation,
Anomaly, Risk signal), each an observation with its reasons - never a verdict on anyone. When the risk
snapshot is not ready, `risk` is null and `riskUnavailable` says why.

### `POST /inventory/changes`

One day's binding adds and removes, in key order, a page at a time. A `POST` because the cursor holds the
last row's identifiers.

```json
{ "date": "2026-10-31", "limit": 1000 }
```

```json
{
  "date": "2026-10-31",
  "changes": [
    { "msisdn": "9120000001", "imsi": "432110000000001", "iccid": "8998110000000000001", "imei": "35085748000001", "tac": "35085748", "change": "add", "delivery": 250 }
  ],
  "next": "OTEyMDAwMDAwMTo0MzIxMTAwMDAwMDAwMDE6MzUwODU3NDgwMDAwMDE6MjUw",
  "identifiers": "full"
}
```

Send `next` back as `cursor` for the following page; `null` on the last. `limit` is 1 to 5,000 (1,000 by
default). Rows with no handset are left out, as from every count (ADR-020). Follow an
`inventory.day-imported` event with this endpoint for its `businessDate`.

### `GET /events?after={sequence}&limit={n}`

```json
{
  "events": [
    { "sequence": 41, "id": "6f0d…", "type": "inventory.day-imported", "subject": "2026-10-31", "occurredAt": "2026-11-01T06:12:40Z",
      "payload": { "businessDate": "2026-10-31", "jobId": 812, "status": "COMPLETED", "rowsImported": 8204417, "rowsRejected": 0, "replacesJobId": null } },
    { "sequence": 42, "id": "a91c…", "type": "catalogue.version-activated", "subject": "v2026.11.01", "occurredAt": "2026-11-02T09:00:05Z",
      "payload": { "versionLabel": "v2026.11.01", "tacVersionId": 8, "replacesVersionLabel": "v2026.10.04" } }
  ],
  "next": 42,
  "oldestAvailable": 1,
  "latest": 42
}
```

Start at `after=0`; keep `next` and send it as the next `after`. If your cursor is below `oldestAvailable`
(events are kept 30 days), events were missed: resynchronise from `/inventory/changes`. `limit` is 1 to 500
(100 by default).

| Event | When | Follow with |
|---|---|---|
| `inventory.day-imported` | A day of the binding feed became effective: folded into current state and history. `replacesJobId` set when it replaced an earlier import of the same day | `POST /inventory/changes` for `businessDate` |
| `catalogue.version-activated` | A GSMA catalogue version became the active one | `GET /devices/models/{tac}` for the models you hold |

### `POST /events/declared`

Lodestone delivers CRM's events. Idempotent by `eventId`, per client.

```json
{
  "events": [
    {
      "eventId": "crm-000123",
      "source": "crm",
      "type": "sim-swap",
      "occurredAt": "2026-08-31T09:15:00+03:30",
      "msisdn": "9120000001",
      "imsi": "432110000000001",
      "previousImsi": "432110000000002",
      "reference": "ORD-000123"
    }
  ]
}
```

```json
{ "accepted": 1, "duplicates": 0, "rejected": 0, "results": [ { "index": 0, "eventId": "crm-000123", "outcome": "accepted", "problems": null } ] }
```

| Field | Rule |
|---|---|
| `eventId` | Required, at most 128 characters: the idempotency key |
| `source` | Required: the declaring system, letters, digits and hyphens |
| `type` | `sim-swap`, `activation` or `deactivation` |
| `occurredAt` | Required, with an offset; not more than five minutes in the future |
| `msisdn` | Required, ten digits |
| `imsi` / `iccid` | The SIM, by either (an ICCID of 18 to 20 digits, stored as declared) |
| `previousImsi` / `previousIccid` | Required for a swap, refused otherwise |
| `reference` | Optional, opaque to PI |

| Outcome | Meaning | HTTP |
|---|---|---|
| `accepted` | Stored | 200 when every event is accepted or a duplicate |
| `duplicate` | Stored before with the same content: a safe retry | 200 |
| `conflict` | The id was used before for different content | 422 |
| `invalid` | Refused; `problems` names each field | 422 |

At most 100 events a request. The valid events of a delivery are stored whatever their neighbours did; a
`422` says that at least one needs the sender's attention.

### `GET /quality/feed?from={date}&to={date}` and `GET /quality/signals`

The Data quality page's figures: each daily file judged against the ordinary days, and the signals over the
current state. Aggregates only. The shapes are the UI's own (`FeedQualityResponse`, `QualitySignalsResponse`
in the OpenAPI document).

## A client's first hour

```bash
# 1. A token (the secret comes from the administrator who registered the client, once).
curl -s -u "lodestone:$PI_CLIENT_SECRET" -d grant_type=client_credentials https://pi.example.internal/api/integration/v1/oauth/token

# 2. What PI holds, and whether lookups can be answered.
curl -s -H "Authorization: Bearer $TOKEN" https://pi.example.internal/api/integration/v1/status

# 3. A lookup.
curl -s -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
     -d '{"identifier":"9120000001"}' https://pi.example.internal/api/integration/v1/inventory/lookup

# 4. Follow the feed: every new day, then its changes.
curl -s -H "Authorization: Bearer $TOKEN" "https://pi.example.internal/api/integration/v1/events?after=0"
```

`pi.example.internal` stands for the address MCI's platform gives the service; the real one is not in this
repository.
