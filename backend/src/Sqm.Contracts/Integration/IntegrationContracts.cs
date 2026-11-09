using System.Text.Json;
using System.Text.Json.Serialization;
using Sqm.Contracts.Risk;
using Sqm.Contracts.Timeline;

namespace Sqm.Contracts.Integration;

// --------------------------------------------------------------------- OAuth 2.0 (RFC 6749)

/// <summary>An access token, as RFC 6749 §5.1 writes it.</summary>
/// <param name="AccessToken">The bearer token.</param>
/// <param name="TokenType">Always <c>Bearer</c>.</param>
/// <param name="ExpiresIn">Seconds until it stops working.</param>
/// <param name="Scope">The scopes it carries, space-separated.</param>
public sealed record TokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("token_type")] string TokenType,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("scope")] string Scope);

/// <summary>A refused token request, as RFC 6749 §5.2 writes it.</summary>
/// <param name="Error">invalid_request, invalid_client, invalid_scope or unsupported_grant_type.</param>
/// <param name="ErrorDescription">Why, in a sentence.</param>
public sealed record TokenError(
    [property: JsonPropertyName("error")] string Error,
    [property: JsonPropertyName("error_description")] string ErrorDescription);

// --------------------------------------------------------------------- requests

/// <summary>One identifier to resolve. In the body, never the URL (MCI red lines, §2-3 row 7).</summary>
/// <param name="Identifier">A phone number (10 digits), an IMSI (15), an ICCID (19) or an IMEI (14).</param>
/// <param name="Kind">msisdn, imsi, iccid or imei; read from the length when left out.</param>
public sealed record IdentifierRequest(string? Identifier, string? Kind = null);

/// <summary>Several identifiers at once.</summary>
/// <param name="Identifiers">At most the configured batch size.</param>
public sealed record BatchLookupRequest(IReadOnlyList<IdentifierRequest>? Identifiers);

/// <summary>One declared event, as Lodestone delivers it from CRM.</summary>
/// <param name="EventId">The sender's id, unique per client: the idempotency key.</param>
/// <param name="Source">The system that declared it, such as <c>crm</c>.</param>
/// <param name="Type">sim-swap, activation or deactivation.</param>
/// <param name="OccurredAt">When it happened, ISO 8601 with an offset.</param>
/// <param name="Msisdn">The number.</param>
/// <param name="Imsi">The SIM by IMSI, or leave it out and name it by ICCID.</param>
/// <param name="Iccid">The SIM by ICCID.</param>
/// <param name="PreviousImsi">For a swap: the SIM the number left, by IMSI.</param>
/// <param name="PreviousIccid">For a swap: the SIM the number left, by ICCID.</param>
/// <param name="Reference">The sender's own reference, such as an order number. Opaque to PI.</param>
public sealed record DeclaredEventInput(
    string? EventId, string? Source, string? Type, DateTimeOffset? OccurredAt, string? Msisdn,
    string? Imsi = null, string? Iccid = null, string? PreviousImsi = null, string? PreviousIccid = null,
    string? Reference = null);

/// <summary>A delivery of declared events.</summary>
/// <param name="Events">At most the configured number per request.</param>
public sealed record DeclaredEventsDelivery(IReadOnlyList<DeclaredEventInput>? Events);

// --------------------------------------------------------------------- inventory

/// <summary>What PI knows of one identifier: its bindings, now and before.</summary>
/// <param name="Kind">msisdn, imsi, iccid or imei.</param>
/// <param name="Identifier">As sent.</param>
/// <param name="ResolvedImsi">For an ICCID, the IMSI the operator's numbering derives it from.</param>
/// <param name="Found">Whether any binding holds it.</param>
/// <param name="Summary">Counts across its bindings.</param>
/// <param name="Bindings">Its bindings, most recently changed first.</param>
/// <param name="Truncated">More bindings exist than were returned.</param>
/// <param name="Identifiers">full, or masked when the token lacks identifiers.reveal.</param>
/// <param name="DataThrough">The last day of the binding feed PI holds.</param>
public sealed record InventoryLookupResponse(
    string Kind, string Identifier, string? ResolvedImsi, bool Found, InventorySummary Summary,
    IReadOnlyList<InventoryBindingInfo> Bindings, bool Truncated, string Identifiers, string? DataThrough);

/// <summary>Counts across one identifier's bindings.</summary>
/// <param name="Bindings">Number + SIM + handset combinations ever held.</param>
/// <param name="ActiveBindings">Those still active.</param>
/// <param name="Numbers">Distinct numbers among them.</param>
/// <param name="Sims">Distinct SIMs.</param>
/// <param name="Handsets">Distinct handsets.</param>
/// <param name="FirstSeen">The first day the feed named it.</param>
/// <param name="FirstSeenIsDumpWindow">The first day is the initial dump's, which covers a month, not a day.</param>
/// <param name="LastChange">The last day one of its bindings changed.</param>
public sealed record InventorySummary(
    int Bindings, int ActiveBindings, int Numbers, int Sims, int Handsets, string? FirstSeen, bool FirstSeenIsDumpWindow,
    string? LastChange);

/// <summary>One binding: a number, a SIM and a handset together.</summary>
/// <param name="Msisdn">The number.</param>
/// <param name="Imsi">The SIM.</param>
/// <param name="Iccid">The SIM's ICCID, derived from the IMSI by the operator's numbering; null when it does not apply.</param>
/// <param name="IccidSource">Always <c>derived</c>: no feed PI receives carries ICCIDs.</param>
/// <param name="Imei">The handset.</param>
/// <param name="Tac">The handset's model: its first eight digits.</param>
/// <param name="Brand">The model's brand, from the active GSMA catalogue.</param>
/// <param name="Model">The model's marketing name.</param>
/// <param name="Active">Still active.</param>
/// <param name="FirstSeen">The first day the feed named this binding.</param>
/// <param name="FirstSeenIsDumpWindow">That day is the initial dump's.</param>
/// <param name="LastChange">The last day it changed.</param>
public sealed record InventoryBindingInfo(
    string Msisdn, string Imsi, string? Iccid, string IccidSource, string Imei, string? Tac, string? Brand, string? Model,
    bool Active, string? FirstSeen, bool FirstSeenIsDumpWindow, string? LastChange);

/// <summary>One identifier of a batch, resolved or refused.</summary>
/// <param name="Index">Its place in the request.</param>
/// <param name="Outcome">found, notFound or invalid.</param>
/// <param name="Problem">For an invalid one, why.</param>
/// <param name="Result">For a valid one, the lookup.</param>
public sealed record BatchLookupItem(int Index, string Outcome, string? Problem, InventoryLookupResponse? Result);

/// <summary>A batch of lookups.</summary>
/// <param name="Items">One per identifier sent, in order.</param>
public sealed record BatchLookupResponse(IReadOnlyList<BatchLookupItem> Items);

/// <summary>One identifier's history: every binding over time, and what was declared about it.</summary>
/// <param name="Kind">msisdn, imsi, iccid or imei.</param>
/// <param name="Identifier">As sent.</param>
/// <param name="ResolvedImsi">For an ICCID, the IMSI it resolves to.</param>
/// <param name="Timeline">The observed history, as the Explorer's timeline draws it.</param>
/// <param name="Declared">What CRM declared about the number or the SIM, newest first; empty for a handset.</param>
public sealed record InventoryHistoryResponse(
    string Kind, string Identifier, string? ResolvedImsi, TimelineResponse Timeline, IReadOnlyList<DeclaredEventInfo> Declared);

/// <summary>A declared event as PI answers it.</summary>
/// <param name="EventId">The sender's id.</param>
/// <param name="Source">The system that declared it.</param>
/// <param name="Type">sim-swap, activation or deactivation.</param>
/// <param name="OccurredAt">When it happened.</param>
/// <param name="ReceivedAt">When PI received it.</param>
/// <param name="Msisdn">The number.</param>
/// <param name="Imsi">The SIM by IMSI.</param>
/// <param name="Iccid">The SIM by ICCID.</param>
/// <param name="PreviousImsi">For a swap, the SIM left.</param>
/// <param name="PreviousIccid">For a swap, the SIM left.</param>
/// <param name="Reference">The sender's reference.</param>
/// <param name="DeliveredBy">The client that delivered it.</param>
public sealed record DeclaredEventInfo(
    string EventId, string Source, string Type, string OccurredAt, string ReceivedAt, string Msisdn, string? Imsi, string? Iccid,
    string? PreviousImsi, string? PreviousIccid, string? Reference, string DeliveredBy);

/// <summary>The outcome of one declared event delivered.</summary>
/// <param name="Index">Its place in the delivery.</param>
/// <param name="EventId">Its id, as sent.</param>
/// <param name="Outcome">accepted, duplicate, conflict or invalid.</param>
/// <param name="Problems">For an invalid one, why, by field.</param>
public sealed record DeclaredEventResult(int Index, string? EventId, string Outcome, IReadOnlyDictionary<string, string>? Problems);

/// <summary>The outcome of a delivery of declared events.</summary>
/// <param name="Accepted">Stored now.</param>
/// <param name="Duplicates">Stored before with the same content: retries, which are safe.</param>
/// <param name="Rejected">Invalid, or an id used before for different content. The others were stored.</param>
/// <param name="Results">One per event, in order.</param>
public sealed record DeclaredEventsResponse(int Accepted, int Duplicates, int Rejected, IReadOnlyList<DeclaredEventResult> Results);

// --------------------------------------------------------------------- devices

/// <summary>A device model: what a TAC is, from the active GSMA catalogue.</summary>
/// <param name="Tac">The eight digits.</param>
/// <param name="KnownToGsma">Whether the active catalogue lists it; seen on the network but unlisted otherwise.</param>
/// <param name="Manufacturer">The manufacturer.</param>
/// <param name="Brand">The brand.</param>
/// <param name="Model">The model name.</param>
/// <param name="MarketingName">The marketing name.</param>
/// <param name="DeviceType">The GSMA device type: Smartphone, Modem, IoT Device and so on.</param>
/// <param name="OperatingSystem">The operating system.</param>
/// <param name="Capabilities">What the catalogue says it can do.</param>
/// <param name="AllocationDate">When GSMA allocated the TAC.</param>
/// <param name="CatalogueVersionId">The catalogue version answering.</param>
public sealed record DeviceModelInfo(
    string Tac, bool KnownToGsma, string? Manufacturer, string? Brand, string? Model, string? MarketingName, string? DeviceType,
    string? OperatingSystem, DeviceCapabilitiesInfo Capabilities, string? AllocationDate, int CatalogueVersionId);

/// <summary>A model's capabilities, as the GSMA catalogue records them; null where it does not say.</summary>
/// <param name="Lte">LTE.</param>
/// <param name="FiveG">5G.</param>
/// <param name="Esim">An eUICC.</param>
/// <param name="ImsEmergency">IMS emergency calling. Not VoLTE: the catalogue does not hold VoLTE.</param>
/// <param name="Bands">The band list.</param>
/// <param name="SimSlots">SIM slots.</param>
/// <param name="ImeiQuantity">IMEIs the model carries: two for a dual-SIM handset.</param>
/// <param name="Nfc">NFC.</param>
/// <param name="Wlan">WLAN.</param>
/// <param name="Bluetooth">Bluetooth.</param>
public sealed record DeviceCapabilitiesInfo(
    bool? Lte, bool? FiveG, bool? Esim, bool? ImsEmergency, string? Bands, string? SimSlots, string? ImeiQuantity,
    string? Nfc, string? Wlan, string? Bluetooth);

// --------------------------------------------------------------------- facts

/// <summary>
/// The facts a policy decision is written against (Tessera): what PI observed, never a decision. Whether
/// something is eligible, blocked or grey-listed is Tessera's to say.
/// </summary>
/// <param name="Kind">msisdn, imsi, iccid or imei.</param>
/// <param name="Identifier">As sent.</param>
/// <param name="ResolvedImsi">For an ICCID, the IMSI it resolves to.</param>
/// <param name="Found">Whether any binding holds it.</param>
/// <param name="Summary">Counts across its bindings.</param>
/// <param name="NetworkAgeDays">Days since the feed first named it; null when it came from the initial dump.</param>
/// <param name="Devices">The handsets of its active bindings, with their models and capabilities.</param>
/// <param name="Risk">PI's risk measures for it and the evidence level they reach - an observation, not a verdict; null when they are unavailable.</param>
/// <param name="RiskUnavailable">Why risk measures are missing, when they are.</param>
/// <param name="Declared">What CRM declared about the number or the SIM, newest first.</param>
/// <param name="Identifiers">full, or masked when the token lacks identifiers.reveal.</param>
/// <param name="DataThrough">The last day of the binding feed PI holds.</param>
public sealed record InventoryFactsResponse(
    string Kind, string Identifier, string? ResolvedImsi, bool Found, InventorySummary Summary, int? NetworkAgeDays,
    IReadOnlyList<DeviceFactsInfo> Devices, RiskEntityResponse? Risk, string? RiskUnavailable,
    IReadOnlyList<DeclaredEventInfo> Declared, string Identifiers, string? DataThrough);

/// <summary>A handset in an active binding, and its model.</summary>
/// <param name="Imei">The handset.</param>
/// <param name="Model">Its model; null when the TAC is not in the catalogue and the network has not counted it.</param>
public sealed record DeviceFactsInfo(string Imei, DeviceModelInfo? Model);

// --------------------------------------------------------------------- sync

/// <summary>A page of one day's binding changes to read.</summary>
/// <param name="Date">The business date, yyyy-MM-dd.</param>
/// <param name="Cursor">The <c>next</c> of the previous page; left out for the first.</param>
/// <param name="Limit">Changes in the page; the configured default when left out.</param>
public sealed record BindingChangesRequest(string? Date, string? Cursor = null, int? Limit = null);

/// <summary>A page of one day's binding changes.</summary>
/// <param name="Date">The business date.</param>
/// <param name="Changes">The changes, in key order.</param>
/// <param name="Next">The cursor for the next page, or null on the last.</param>
/// <param name="Identifiers">full, or masked when the token lacks identifiers.reveal.</param>
public sealed record BindingChangesResponse(string Date, IReadOnlyList<BindingChangeInfo> Changes, string? Next, string Identifiers);

/// <summary>One binding added or removed.</summary>
/// <param name="Msisdn">The number.</param>
/// <param name="Imsi">The SIM.</param>
/// <param name="Iccid">The SIM's derived ICCID.</param>
/// <param name="Imei">The handset.</param>
/// <param name="Tac">The handset's model.</param>
/// <param name="Change">add or remove.</param>
/// <param name="Delivery">The delivery sequence it came in.</param>
public sealed record BindingChangeInfo(string Msisdn, string Imsi, string? Iccid, string Imei, string? Tac, string Change, int Delivery);

// --------------------------------------------------------------------- events

/// <summary>A page of PI's event feed.</summary>
/// <param name="Events">The events after the cursor, oldest first.</param>
/// <param name="Next">The cursor to send next: the last sequence returned, or the one sent when nothing was.</param>
/// <param name="OldestAvailable">The lowest sequence still kept; a cursor below it means events were missed.</param>
/// <param name="Latest">The highest sequence issued so far.</param>
public sealed record IntegrationEventPage(IReadOnlyList<IntegrationEventInfo> Events, long Next, long? OldestAvailable, long Latest);

/// <summary>One published event.</summary>
/// <param name="Sequence">Its place in the feed.</param>
/// <param name="Id">Its id, for de-duplication.</param>
/// <param name="Type">inventory.day-imported or catalogue.version-activated.</param>
/// <param name="Subject">What it is about.</param>
/// <param name="OccurredAt">When it was recorded.</param>
/// <param name="Payload">Its detail.</param>
public sealed record IntegrationEventInfo(long Sequence, Guid Id, string Type, string Subject, string OccurredAt, JsonElement Payload);

// --------------------------------------------------------------------- status

/// <summary>The state of PI's data, for a client deciding whether to read now.</summary>
/// <param name="ApiVersion">The integration API's version.</param>
/// <param name="Client">The calling client.</param>
/// <param name="Scopes">The scopes its token carries.</param>
/// <param name="DataThrough">The last day of the binding feed PI holds.</param>
/// <param name="History">Ready, Building or NotDeployed: whether lookups and history can be answered.</param>
/// <param name="Sources">Each feed's freshness.</param>
public sealed record IntegrationStatusResponse(
    string ApiVersion, string Client, IReadOnlyList<string> Scopes, string? DataThrough, string History,
    IReadOnlyList<SourceStatusInfo> Sources);

/// <summary>One feed's freshness.</summary>
/// <param name="Source">SQM (the binding feed) or TAC (the GSMA catalogue).</param>
/// <param name="LatestBusinessDate">The newest day imported.</param>
/// <param name="DaysBehind">Days between it and today.</param>
/// <param name="MissingDays">Days the source never delivered.</param>
public sealed record SourceStatusInfo(string Source, string? LatestBusinessDate, int? DaysBehind, int MissingDays);

// --------------------------------------------------------------------- administration

/// <summary>A registered client, for an administrator: never its secret.</summary>
/// <param name="Id">Its key.</param>
/// <param name="ClientId">The name it authenticates with.</param>
/// <param name="DisplayName">What a person reads.</param>
/// <param name="Description">Why it is registered.</param>
/// <param name="Scopes">What it may ask for.</param>
/// <param name="IsActive">False once deactivated.</param>
/// <param name="SecretSetAt">When its secret was issued.</param>
/// <param name="LastTokenAt">When it last received a token.</param>
/// <param name="CreatedAt">When it was registered.</param>
/// <param name="CreatedBy">By whom.</param>
/// <param name="UpdatedAt">When it last changed.</param>
/// <param name="UpdatedBy">By whom.</param>
public sealed record IntegrationClientInfo(
    long Id, string ClientId, string DisplayName, string Description, IReadOnlyList<string> Scopes, bool IsActive,
    string SecretSetAt, string? LastTokenAt, string CreatedAt, string CreatedBy, string UpdatedAt, string UpdatedBy);

/// <summary>A client with a secret just issued: shown once, never again.</summary>
/// <param name="Client">The client.</param>
/// <param name="ClientSecret">The secret.</param>
public sealed record IntegrationClientSecretResponse(IntegrationClientInfo Client, string ClientSecret);

/// <summary>A scope an administrator may grant.</summary>
/// <param name="Scope">Its code.</param>
/// <param name="Description">What it allows.</param>
public sealed record IntegrationScopeInfo(string Scope, string Description);

/// <summary>The clients and the scopes there are.</summary>
/// <param name="Clients">Every registered client.</param>
/// <param name="Scopes">Every scope.</param>
public sealed record IntegrationClientList(IReadOnlyList<IntegrationClientInfo> Clients, IReadOnlyList<IntegrationScopeInfo> Scopes);

/// <summary>A client to register.</summary>
/// <param name="ClientId">Lower-case letters, digits and hyphens, 3 to 48, starting with a letter.</param>
/// <param name="DisplayName">What a person reads.</param>
/// <param name="Description">Why it is registered.</param>
/// <param name="Scopes">What it may ask for.</param>
public sealed record IntegrationClientCreateRequest(string? ClientId, string? DisplayName, string? Description, IReadOnlyList<string>? Scopes);

/// <summary>A change to a client.</summary>
/// <param name="DisplayName">What a person reads.</param>
/// <param name="Description">Why it is registered.</param>
/// <param name="Scopes">What it may ask for.</param>
/// <param name="IsActive">False deactivates it and ends its tokens.</param>
public sealed record IntegrationClientUpdateRequest(string? DisplayName, string? Description, IReadOnlyList<string>? Scopes, bool IsActive);
