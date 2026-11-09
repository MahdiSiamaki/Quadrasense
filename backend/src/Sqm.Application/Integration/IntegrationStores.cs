namespace Sqm.Application.Integration;

/// <summary>The registered clients of the integration API and the tokens issued to them (ADR-024).</summary>
/// <remarks>
/// Secrets and tokens are 32 random bytes; only their SHA-256 is stored (MCI red lines, §2-3 row 8).
/// Deactivating a client or rotating its secret ends every token it holds, in the same transaction.
/// </remarks>
public interface IIntegrationClientStore
{
    /// <summary>Every registered client, active or not.</summary>
    Task<IReadOnlyList<IntegrationClient>> ListAsync(CancellationToken ct);

    /// <summary>One client, or null.</summary>
    Task<IntegrationClient?> GetAsync(long id, CancellationToken ct);

    /// <summary>Registers a client and issues its first secret; null when the client id is taken.</summary>
    Task<IssuedSecret?> CreateAsync(NewIntegrationClient client, string actor, CancellationToken ct);

    /// <summary>Changes a client; null when it does not exist.</summary>
    Task<IntegrationClient?> UpdateAsync(long id, IntegrationClientChange change, string actor, CancellationToken ct);

    /// <summary>Issues a new secret, ending the old one and every token; null when the client does not exist.</summary>
    Task<IssuedSecret?> RotateSecretAsync(long id, string actor, CancellationToken ct);

    /// <summary>
    /// The client credentials grant: checks the client and its secret, and issues a token carrying the
    /// scopes asked for, or all the client holds when none were named.
    /// </summary>
    Task<TokenGrant> GrantAsync(string clientId, string secret, IReadOnlySet<string>? requested, CancellationToken ct);

    /// <summary>The client a bearer token belongs to, or null when it is unknown, expired or its client is inactive.</summary>
    Task<AuthenticatedClient?> ResolveAsync(string token, CancellationToken ct);

    /// <summary>Deletes expired tokens; returns how many.</summary>
    Task<int> DeleteExpiredTokensAsync(CancellationToken ct);
}

/// <summary>Declared events: what CRM declared, delivered by Lodestone.</summary>
public interface IDeclaredEventStore
{
    /// <summary>
    /// Stores an event once per sender id: a retry with the same content is a duplicate, the same id with
    /// different content a conflict.
    /// </summary>
    Task<DeclaredEventOutcome> RecordAsync(long clientPk, DeclaredEvent declared, CancellationToken ct);

    /// <summary>
    /// The events naming a number or a SIM (by IMSI or ICCID, as the current or the previous SIM), newest
    /// first.
    /// </summary>
    Task<IReadOnlyList<StoredDeclaredEvent>> ListForAsync(
        string? msisdn, string? imsi, string? iccid, int limit, CancellationToken ct);
}

/// <summary>PI's published events, in commit order, for Lodestone to pull.</summary>
public interface IIntegrationOutbox
{
    /// <summary>Appends an event in a transaction of its own.</summary>
    Task<OutboxEvent> AppendAsync(string type, string subject, object payload, CancellationToken ct);

    /// <summary>The events after a sequence, oldest first.</summary>
    Task<OutboxPage> ReadAsync(long after, int limit, CancellationToken ct);

    /// <summary>Deletes events recorded before a moment; returns how many.</summary>
    Task<int> PruneAsync(DateTimeOffset before, CancellationToken ct);
}

/// <summary>One day's binding adds and removes, from the event log, for incremental synchronisation.</summary>
public interface IBindingChangeReader
{
    /// <summary>
    /// A page of a day's changes in key order, starting after a cursor. Rows with no handset are left
    /// out, as everywhere they are counted (ADR-020).
    /// </summary>
    Task<BindingChangePage> ReadAsync(DateOnly day, BindingChangeCursor? after, int limit, CancellationToken ct);
}
