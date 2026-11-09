using System.Text.Json;

namespace Sqm.Application.Integration;

/// <summary>A system registered to call the integration API, as an administrator sees it: never its secret.</summary>
/// <param name="Id">The row's key.</param>
/// <param name="ClientId">The public name it authenticates with, such as <c>lodestone</c>.</param>
/// <param name="DisplayName">What a person reads in the audit log and the settings page.</param>
/// <param name="Description">Why it is registered.</param>
/// <param name="Scopes">What it may ask for.</param>
/// <param name="IsActive">False once deactivated: its tokens stop working at once.</param>
/// <param name="SecretSetAt">When its current secret was issued.</param>
/// <param name="LastTokenAt">When it last received a token, or null if it never has.</param>
/// <param name="CreatedAt">When it was registered.</param>
/// <param name="CreatedBy">Who registered it.</param>
/// <param name="UpdatedAt">When it was last changed.</param>
/// <param name="UpdatedBy">Who last changed it.</param>
public sealed record IntegrationClient(
    long Id, string ClientId, string DisplayName, string Description, IReadOnlyList<string> Scopes, bool IsActive,
    DateTimeOffset SecretSetAt, DateTimeOffset? LastTokenAt, DateTimeOffset CreatedAt, string CreatedBy,
    DateTimeOffset UpdatedAt, string UpdatedBy);

/// <summary>A client authenticated by a bearer token, for the length of one request.</summary>
/// <param name="Id">The client's row key.</param>
/// <param name="ClientId">Its public name.</param>
/// <param name="DisplayName">Its display name.</param>
/// <param name="Scopes">The scopes the token carries: those asked for when it was issued, never more than the client holds.</param>
public sealed record AuthenticatedClient(long Id, string ClientId, string DisplayName, IReadOnlySet<string> Scopes)
{
    /// <summary>Whether the token carries a scope.</summary>
    public bool Has(string scope) => Scopes.Contains(scope);

    /// <summary>How the audit log names a client, so that it is never mistaken for a person.</summary>
    public string Actor => $"client:{ClientId}";
}

/// <summary>A client to register.</summary>
/// <param name="ClientId">Its public name: lower-case letters, digits and hyphens, 3 to 48, starting with a letter.</param>
/// <param name="DisplayName">What a person reads.</param>
/// <param name="Description">Why it is registered.</param>
/// <param name="Scopes">What it may ask for.</param>
public sealed record NewIntegrationClient(string ClientId, string DisplayName, string Description, IReadOnlyList<string> Scopes);

/// <summary>A change to a registered client.</summary>
/// <param name="DisplayName">What a person reads.</param>
/// <param name="Description">Why it is registered.</param>
/// <param name="Scopes">What it may ask for from now on; tokens already issued keep theirs until they expire.</param>
/// <param name="IsActive">False deactivates it, ending every token it holds.</param>
public sealed record IntegrationClientChange(string DisplayName, string Description, IReadOnlyList<string> Scopes, bool IsActive);

/// <summary>A client with a secret just issued: the only time the secret exists outside its hash.</summary>
/// <param name="Client">The client.</param>
/// <param name="Secret">The secret, to be handed to the system's operator once.</param>
public sealed record IssuedSecret(IntegrationClient Client, string Secret);

/// <summary>How a client credentials grant ended.</summary>
public enum TokenGrantOutcome
{
    /// <summary>A token was issued.</summary>
    Issued,

    /// <summary>Unknown client, wrong secret or a deactivated client - not told apart to the caller.</summary>
    InvalidClient,

    /// <summary>A scope was asked for that the client does not hold.</summary>
    InvalidScope,
}

/// <summary>The outcome of a client credentials grant.</summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="AccessToken">The bearer token, when issued.</param>
/// <param name="Scopes">The scopes it carries.</param>
/// <param name="ExpiresAt">When it stops working.</param>
/// <param name="ClientPk">The client's row key when it was identified, for the audit log.</param>
public sealed record TokenGrant(
    TokenGrantOutcome Outcome, string? AccessToken, IReadOnlySet<string> Scopes, DateTimeOffset? ExpiresAt, long? ClientPk);

/// <summary>What CRM declared, as Lodestone delivered it.</summary>
public enum DeclaredEventType
{
    /// <summary>The number moved from one SIM to another.</summary>
    SimSwap,

    /// <summary>The number was activated on a SIM.</summary>
    Activation,

    /// <summary>The number was deactivated.</summary>
    Deactivation,
}

/// <summary>A declared event: a commercial fact, keyed by PI's own identifiers.</summary>
/// <param name="EventId">The sender's id, unique per client: the idempotency key.</param>
/// <param name="Source">The system that declared it, such as <c>crm</c>.</param>
/// <param name="Type">What was declared.</param>
/// <param name="OccurredAt">When it happened, as the source says.</param>
/// <param name="Msisdn">The number, ten digits.</param>
/// <param name="Imsi">The SIM, when the source names it by IMSI.</param>
/// <param name="Iccid">The SIM, when the source names it by ICCID.</param>
/// <param name="PreviousImsi">For a swap: the SIM it left, by IMSI.</param>
/// <param name="PreviousIccid">For a swap: the SIM it left, by ICCID.</param>
/// <param name="Reference">The source's own reference, such as an order number: opaque to PI.</param>
public sealed record DeclaredEvent(
    string EventId, string Source, DeclaredEventType Type, DateTimeOffset OccurredAt, string Msisdn,
    string? Imsi, string? Iccid, string? PreviousImsi, string? PreviousIccid, string? Reference);

/// <summary>A declared event as stored.</summary>
/// <param name="Id">The row's key.</param>
/// <param name="ClientId">The client that delivered it.</param>
/// <param name="Event">The event.</param>
/// <param name="ReceivedAt">When PI received it.</param>
public sealed record StoredDeclaredEvent(long Id, string ClientId, DeclaredEvent Event, DateTimeOffset ReceivedAt);

/// <summary>What became of a declared event delivered to PI.</summary>
public enum DeclaredEventOutcome
{
    /// <summary>Stored.</summary>
    Accepted,

    /// <summary>Already stored with the same content: a retry, accepted again and stored once.</summary>
    Duplicate,

    /// <summary>The id was used before for different content: refused.</summary>
    Conflict,
}

/// <summary>One event PI published.</summary>
/// <param name="Sequence">Its place in the feed, and the cursor a reader resumes after.</param>
/// <param name="EventId">Its id, for a reader that de-duplicates.</param>
/// <param name="Type">What happened, such as <c>inventory.day-imported</c>.</param>
/// <param name="Subject">What it is about: a business date, a catalogue version.</param>
/// <param name="OccurredAt">When it was recorded.</param>
/// <param name="Payload">The detail, as JSON.</param>
public sealed record OutboxEvent(long Sequence, Guid EventId, string Type, string Subject, DateTimeOffset OccurredAt, JsonElement Payload);

/// <summary>A page of the event feed.</summary>
/// <param name="Events">The events after the cursor, in order.</param>
/// <param name="OldestAvailable">The lowest sequence still kept, or null when the feed is empty: a reader whose cursor is older has missed events.</param>
/// <param name="Latest">The highest sequence written so far, 0 when none.</param>
public sealed record OutboxPage(IReadOnlyList<OutboxEvent> Events, long? OldestAvailable, long Latest);

/// <summary>The kinds of event PI publishes.</summary>
public static class IntegrationEventTypes
{
    /// <summary>A day of the binding feed was imported and folded into current state and history.</summary>
    public const string DayImported = "inventory.day-imported";

    /// <summary>A GSMA catalogue version became the active one: models and capabilities may have changed.</summary>
    public const string CatalogueActivated = "catalogue.version-activated";
}

/// <summary>One binding added or removed on a day of the feed.</summary>
/// <param name="Date">The business date.</param>
/// <param name="Sequence">The delivery it came in.</param>
/// <param name="Msisdn">The number.</param>
/// <param name="Imsi">The SIM.</param>
/// <param name="Imei">The handset.</param>
/// <param name="Tac">The handset's model, its first eight digits.</param>
/// <param name="Added">True for an add, false for a remove.</param>
public sealed record DailyBindingChange(DateOnly Date, int Sequence, ulong Msisdn, ulong Imsi, string Imei, string? Tac, bool Added);

/// <summary>Where a page of a day's changes ends: the key of its last row.</summary>
/// <param name="Msisdn">The number.</param>
/// <param name="Imsi">The SIM.</param>
/// <param name="Imei">The handset.</param>
/// <param name="Sequence">The delivery.</param>
public sealed record BindingChangeCursor(ulong Msisdn, ulong Imsi, string Imei, int Sequence);

/// <summary>A page of one day's binding changes.</summary>
/// <param name="Changes">The changes, in key order.</param>
/// <param name="Next">Where the next page starts, or null on the last page.</param>
public sealed record BindingChangePage(IReadOnlyList<DailyBindingChange> Changes, BindingChangeCursor? Next);
