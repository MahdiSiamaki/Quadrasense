using System.Data;
using System.Text.Json;
using Dapper;
using Npgsql;
using Sqm.Application.Integration;
using Sqm.Infrastructure.Identity;

namespace Sqm.Infrastructure.Integration;

/// <summary>PI's event feed in PostgreSQL (migration 025, ADR-024): a transactional outbox Lodestone pulls.</summary>
public sealed class PostgresIntegrationOutbox : IIntegrationOutbox
{
    private readonly IdentityDataSource _db;

    /// <summary>Creates the outbox.</summary>
    public PostgresIntegrationOutbox(IdentityDataSource db) => _db = db;

    /// <inheritdoc />
    public async Task<OutboxEvent> AppendAsync(string type, string subject, object payload, CancellationToken ct)
    {
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        var appended = await IntegrationOutboxSql.AppendAsync(
            connection, transaction, type, subject, JsonSerializer.Serialize(payload, IntegrationOutboxSql.Json), ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return appended;
    }

    /// <inheritdoc />
    public async Task<OutboxPage> ReadAsync(long after, int limit, CancellationToken ct)
    {
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);

        var rows = await connection.QueryAsync<IntegrationOutboxSql.Row>(_db.Command($"""
            SELECT {IntegrationOutboxSql.Columns}
              FROM integration.outbox_event
             WHERE sequence > @after
             ORDER BY sequence
             LIMIT @limit
            """, new { after, limit }, ct)).ConfigureAwait(false);

        var bounds = await connection.QuerySingleAsync<(long? Oldest, long? Latest)>(_db.Command(
            "SELECT min(sequence), max(sequence) FROM integration.outbox_event", null, ct)).ConfigureAwait(false);

        // The latest sequence ever issued, not only the latest kept: a feed pruned to nothing still tells
        // a reader how far it has come.
        var issued = await connection.ExecuteScalarAsync<long>(_db.Command(
            "SELECT CASE WHEN is_called THEN last_value ELSE 0 END FROM integration.outbox_event_sequence_seq", null, ct))
            .ConfigureAwait(false);

        return new OutboxPage([.. rows.Select(r => r.ToEvent())], bounds.Oldest, Math.Max(issued, bounds.Latest ?? 0));
    }

    /// <inheritdoc />
    public async Task<int> PruneAsync(DateTimeOffset before, CancellationToken ct)
    {
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        return await connection.ExecuteAsync(_db.Command(
            "DELETE FROM integration.outbox_event WHERE occurred_at < @before", new { before }, ct)).ConfigureAwait(false);
    }
}

/// <summary>
/// Appending to the outbox inside a transaction someone else opened - a completed import's, so that the
/// event exists if and only if the import committed.
/// </summary>
internal static class IntegrationOutboxSql
{
    /// <summary>
    /// The advisory lock every writer takes before inserting, held to the end of its transaction: sequence
    /// order is then commit order, and a reader past a number never sees a lower one appear.
    /// </summary>
    public const long WriterLock = 0x5153_4F55_5442_4F58; // "QSOUTBOX"

    /// <summary>How payloads are written: camelCase, as every other answer of the API.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The columns of an event, as <see cref="Row"/> reads them.</summary>
    public const string Columns =
        "sequence AS Sequence, event_id AS EventId, type AS Type, subject AS Subject, occurred_at AS OccurredAt, payload::text AS Payload";

    /// <summary>Appends an event in the caller's transaction.</summary>
    public static async Task<OutboxEvent> AppendAsync(
        NpgsqlConnection connection, IDbTransaction transaction, string type, string subject, string payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await connection.ExecuteAsync(new CommandDefinition(
            "SELECT pg_advisory_xact_lock(@key)", new { key = WriterLock }, transaction, cancellationToken: ct)).ConfigureAwait(false);

        var row = await connection.QuerySingleAsync<Row>(new CommandDefinition($"""
            INSERT INTO integration.outbox_event (event_id, type, subject, payload)
            VALUES (@id, @type, @subject, @payload::jsonb)
            RETURNING {Columns}
            """,
            new { id = Guid.NewGuid(), type, subject, payload = payloadJson }, transaction, cancellationToken: ct)).ConfigureAwait(false);

        return row.ToEvent();
    }

    /// <summary>An event as Dapper reads it.</summary>
    public sealed class Row
    {
        /// <summary>The sequence.</summary>
        public long Sequence { get; init; }

        /// <summary>The id.</summary>
        public Guid EventId { get; init; }

        /// <summary>The type.</summary>
        public string Type { get; init; } = string.Empty;

        /// <summary>The subject.</summary>
        public string Subject { get; init; } = string.Empty;

        /// <summary>When it was recorded.</summary>
        public DateTimeOffset OccurredAt { get; init; }

        /// <summary>The payload as JSON text.</summary>
        public string Payload { get; init; } = "{}";

        /// <summary>The event.</summary>
        public OutboxEvent ToEvent()
        {
            using var document = JsonDocument.Parse(Payload);
            return new OutboxEvent(Sequence, EventId, Type, Subject, OccurredAt, document.RootElement.Clone());
        }
    }
}
