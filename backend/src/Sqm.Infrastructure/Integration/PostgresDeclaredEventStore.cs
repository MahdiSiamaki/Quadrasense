using System.Security.Cryptography;
using System.Text;
using Dapper;
using Sqm.Application.Integration;
using Sqm.Infrastructure.Identity;

namespace Sqm.Infrastructure.Integration;

/// <summary>Declared events in PostgreSQL (migration 025, ADR-024): append-only, once per sender id.</summary>
public sealed class PostgresDeclaredEventStore : IDeclaredEventStore
{
    private readonly IdentityDataSource _db;

    /// <summary>Creates the store.</summary>
    public PostgresDeclaredEventStore(IdentityDataSource db) => _db = db;

    /// <inheritdoc />
    public async Task<DeclaredEventOutcome> RecordAsync(long clientPk, DeclaredEvent declared, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(declared);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(DeclaredEventRules.Canonical(declared)));
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);

        // The unique index decides between two deliveries of one id racing each other; the loser reads
        // what the winner stored and compares.
        var inserted = await connection.ExecuteScalarAsync<long?>(_db.Command("""
            INSERT INTO integration.declared_event
                (client_pk, event_id, source, type, occurred_at, msisdn, imsi, iccid, previous_imsi, previous_iccid,
                 reference, payload_sha256)
            VALUES
                (@client, @eventId, @source, @type, @occurredAt, @msisdn, @imsi, @iccid, @previousImsi, @previousIccid,
                 @reference, @hash)
            ON CONFLICT (client_pk, event_id) DO NOTHING
            RETURNING id
            """,
            new
            {
                client = clientPk,
                eventId = declared.EventId,
                source = declared.Source,
                type = DeclaredEventRules.Name(declared.Type),
                occurredAt = declared.OccurredAt,
                msisdn = declared.Msisdn,
                imsi = declared.Imsi,
                iccid = declared.Iccid,
                previousImsi = declared.PreviousImsi,
                previousIccid = declared.PreviousIccid,
                reference = declared.Reference,
                hash,
            }, ct)).ConfigureAwait(false);

        if (inserted is not null)
        {
            return DeclaredEventOutcome.Accepted;
        }

        var stored = await connection.ExecuteScalarAsync<byte[]?>(_db.Command(
            "SELECT payload_sha256 FROM integration.declared_event WHERE client_pk = @client AND event_id = @eventId",
            new { client = clientPk, eventId = declared.EventId }, ct)).ConfigureAwait(false);

        return stored is not null && CryptographicOperations.FixedTimeEquals(stored, hash)
            ? DeclaredEventOutcome.Duplicate
            : DeclaredEventOutcome.Conflict;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StoredDeclaredEvent>> ListForAsync(
        string? msisdn, string? imsi, string? iccid, int limit, CancellationToken ct)
    {
        if (msisdn is null && imsi is null && iccid is null)
        {
            return [];
        }

        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        var rows = await connection.QueryAsync<Row>(_db.Command("""
            SELECT e.id AS Id, c.client_id AS ClientId, e.event_id AS EventId, e.source AS Source, e.type AS Type,
                   e.occurred_at AS OccurredAt, e.received_at AS ReceivedAt, e.msisdn AS Msisdn, e.imsi AS Imsi,
                   e.iccid AS Iccid, e.previous_imsi AS PreviousImsi, e.previous_iccid AS PreviousIccid,
                   e.reference AS Reference
              FROM integration.declared_event e
              JOIN integration.client c ON c.id = e.client_pk
             WHERE (@msisdn::text IS NOT NULL AND e.msisdn = @msisdn)
                OR (@imsi::text IS NOT NULL AND (e.imsi = @imsi OR e.previous_imsi = @imsi))
                OR (@iccid::text IS NOT NULL AND (e.iccid = @iccid OR e.previous_iccid = @iccid))
             ORDER BY e.occurred_at DESC, e.id DESC
             LIMIT @limit
            """, new { msisdn, imsi, iccid, limit }, ct)).ConfigureAwait(false);

        return [.. rows.Select(r => new StoredDeclaredEvent(
            r.Id, r.ClientId,
            new DeclaredEvent(
                r.EventId, r.Source, DeclaredEventRules.TypeOf(r.Type) ?? DeclaredEventType.Activation, r.OccurredAt, r.Msisdn,
                r.Imsi, r.Iccid, r.PreviousImsi, r.PreviousIccid, r.Reference),
            r.ReceivedAt))];
    }

    private sealed class Row
    {
        public long Id { get; init; }

        public string ClientId { get; init; } = string.Empty;

        public string EventId { get; init; } = string.Empty;

        public string Source { get; init; } = string.Empty;

        public string Type { get; init; } = string.Empty;

        public DateTimeOffset OccurredAt { get; init; }

        public DateTimeOffset ReceivedAt { get; init; }

        public string Msisdn { get; init; } = string.Empty;

        public string? Imsi { get; init; }

        public string? Iccid { get; init; }

        public string? PreviousImsi { get; init; }

        public string? PreviousIccid { get; init; }

        public string? Reference { get; init; }
    }
}
