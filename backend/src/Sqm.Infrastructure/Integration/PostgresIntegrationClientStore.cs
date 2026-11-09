using Dapper;
using Microsoft.Extensions.Options;
using Sqm.Application.Integration;
using Sqm.Infrastructure.Identity;

namespace Sqm.Infrastructure.Integration;

/// <summary>Integration clients and their tokens in PostgreSQL (migration 025, ADR-024).</summary>
public sealed class PostgresIntegrationClientStore : IIntegrationClientStore
{
    private const string Columns = """
        id AS Id, client_id AS ClientId, display_name AS DisplayName, description AS Description, scopes AS Scopes,
        is_active AS IsActive, secret_set_at AS SecretSetAt, last_token_at AS LastTokenAt, created_at AS CreatedAt,
        created_by AS CreatedBy, updated_at AS UpdatedAt, updated_by AS UpdatedBy
        """;

    private readonly IdentityDataSource _db;
    private readonly IntegrationOptions _options;
    private readonly TimeProvider _clock;

    /// <summary>Creates the store.</summary>
    public PostgresIntegrationClientStore(IdentityDataSource db, IOptions<IntegrationOptions> options, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        _db = db;
        _options = options.Value;
        _clock = clock;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<IntegrationClient>> ListAsync(CancellationToken ct)
    {
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        var rows = await connection.QueryAsync<ClientRow>(_db.Command(
            $"SELECT {Columns} FROM integration.client ORDER BY client_id", null, ct)).ConfigureAwait(false);
        return [.. rows.Select(r => r.ToClient())];
    }

    /// <inheritdoc />
    public async Task<IntegrationClient?> GetAsync(long id, CancellationToken ct)
    {
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        var row = await connection.QuerySingleOrDefaultAsync<ClientRow>(_db.Command(
            $"SELECT {Columns} FROM integration.client WHERE id = @id", new { id }, ct)).ConfigureAwait(false);
        return row?.ToClient();
    }

    /// <inheritdoc />
    public async Task<IssuedSecret?> CreateAsync(NewIntegrationClient client, string actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);

        var secret = IntegrationCredentials.NewValue();
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);

        // ON CONFLICT DO NOTHING rather than a check first: two administrators registering the same name at
        // once would both pass a check, and the unique index decides anyway.
        var row = await connection.QuerySingleOrDefaultAsync<ClientRow>(_db.Command($"""
            INSERT INTO integration.client (client_id, display_name, description, scopes, secret_hash, created_by, updated_by)
            VALUES (@clientId, @displayName, @description, @scopes, @hash, @actor, @actor)
            ON CONFLICT (client_id) DO NOTHING
            RETURNING {Columns}
            """,
            new
            {
                clientId = client.ClientId,
                displayName = client.DisplayName,
                description = client.Description,
                scopes = client.Scopes.ToArray(),
                hash = IntegrationCredentials.Hash(secret),
                actor,
            }, ct)).ConfigureAwait(false);

        return row is null ? null : new IssuedSecret(row.ToClient(), secret);
    }

    /// <inheritdoc />
    public async Task<IntegrationClient?> UpdateAsync(long id, IntegrationClientChange change, string actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(change);

        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        var row = await connection.QuerySingleOrDefaultAsync<ClientRow>(_db.Command($"""
            UPDATE integration.client
               SET display_name = @displayName, description = @description, scopes = @scopes,
                   is_active = @active, updated_at = now(), updated_by = @actor
             WHERE id = @id
            RETURNING {Columns}
            """,
            new
            {
                id,
                displayName = change.DisplayName,
                description = change.Description,
                scopes = change.Scopes.ToArray(),
                active = change.IsActive,
                actor,
            }, ct, transaction)).ConfigureAwait(false);

        // A deactivated client's tokens are refused by the join in ResolveAsync already; deleting them as
        // well means nothing is left behind that a reactivation would bring back to life.
        if (row is not null && !change.IsActive)
        {
            await connection.ExecuteAsync(_db.Command(
                "DELETE FROM integration.access_token WHERE client_pk = @id", new { id }, ct, transaction)).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return row?.ToClient();
    }

    /// <inheritdoc />
    public async Task<IssuedSecret?> RotateSecretAsync(long id, string actor, CancellationToken ct)
    {
        var secret = IntegrationCredentials.NewValue();

        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        var row = await connection.QuerySingleOrDefaultAsync<ClientRow>(_db.Command($"""
            UPDATE integration.client
               SET secret_hash = @hash, secret_set_at = now(), updated_at = now(), updated_by = @actor
             WHERE id = @id
            RETURNING {Columns}
            """, new { id, hash = IntegrationCredentials.Hash(secret), actor }, ct, transaction)).ConfigureAwait(false);

        if (row is not null)
        {
            await connection.ExecuteAsync(_db.Command(
                "DELETE FROM integration.access_token WHERE client_pk = @id", new { id }, ct, transaction)).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return row is null ? null : new IssuedSecret(row.ToClient(), secret);
    }

    /// <inheritdoc />
    public async Task<TokenGrant> GrantAsync(string clientId, string secret, IReadOnlySet<string>? requested, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(clientId);
        ArgumentNullException.ThrowIfNull(secret);

        var none = new HashSet<string>(StringComparer.Ordinal);
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);

        var client = await connection.QuerySingleOrDefaultAsync<SecretRow>(_db.Command(
            "SELECT id AS Id, secret_hash AS SecretHash, scopes AS Scopes, is_active AS IsActive FROM integration.client WHERE client_id = @clientId",
            new { clientId }, ct)).ConfigureAwait(false);

        // An unknown client is hashed against a throwaway value too, so that it costs what a known one does.
        var matches = IntegrationCredentials.Matches(secret, client?.SecretHash ?? new byte[32]);
        if (client is null || !matches || !client.IsActive)
        {
            return new TokenGrant(TokenGrantOutcome.InvalidClient, null, none, null, client?.Id);
        }

        var held = client.Scopes.ToHashSet(StringComparer.Ordinal);
        var scopes = requested is { Count: > 0 } ? requested : held;
        if (!scopes.All(held.Contains))
        {
            return new TokenGrant(TokenGrantOutcome.InvalidScope, null, none, null, client.Id);
        }

        var token = IntegrationCredentials.NewValue();
        var expires = _clock.GetUtcNow() + _options.TokenLifetime;

        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await connection.ExecuteAsync(_db.Command("""
            INSERT INTO integration.access_token (token_hash, client_pk, scopes, expires_at)
            VALUES (@hash, @client, @scopes, @expires)
            """,
            new { hash = IntegrationCredentials.Hash(token), client = client.Id, scopes = scopes.Order(StringComparer.Ordinal).ToArray(), expires },
            ct, transaction)).ConfigureAwait(false);
        await connection.ExecuteAsync(_db.Command(
            "UPDATE integration.client SET last_token_at = now() WHERE id = @id", new { id = client.Id }, ct, transaction))
            .ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);

        return new TokenGrant(TokenGrantOutcome.Issued, token, scopes, expires, client.Id);
    }

    /// <inheritdoc />
    public async Task<AuthenticatedClient?> ResolveAsync(string token, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(token);
        if (!IntegrationCredentials.IsWellFormed(token))
        {
            return null;
        }

        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        var row = await connection.QuerySingleOrDefaultAsync<TokenRow>(_db.Command("""
            SELECT c.id AS Id, c.client_id AS ClientId, c.display_name AS DisplayName, t.scopes AS Scopes
              FROM integration.access_token t
              JOIN integration.client c ON c.id = t.client_pk
             WHERE t.token_hash = @hash AND t.expires_at > now() AND c.is_active
            """, new { hash = IntegrationCredentials.Hash(token) }, ct)).ConfigureAwait(false);

        return row is null
            ? null
            : new AuthenticatedClient(row.Id, row.ClientId, row.DisplayName, row.Scopes.ToHashSet(StringComparer.Ordinal));
    }

    /// <inheritdoc />
    public async Task<int> DeleteExpiredTokensAsync(CancellationToken ct)
    {
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        return await connection.ExecuteAsync(_db.Command(
            "DELETE FROM integration.access_token WHERE expires_at <= now()", null, ct)).ConfigureAwait(false);
    }

    private sealed class ClientRow
    {
        public long Id { get; init; }

        public string ClientId { get; init; } = string.Empty;

        public string DisplayName { get; init; } = string.Empty;

        public string Description { get; init; } = string.Empty;

        public string[] Scopes { get; init; } = [];

        public bool IsActive { get; init; }

        public DateTimeOffset SecretSetAt { get; init; }

        public DateTimeOffset? LastTokenAt { get; init; }

        public DateTimeOffset CreatedAt { get; init; }

        public string CreatedBy { get; init; } = string.Empty;

        public DateTimeOffset UpdatedAt { get; init; }

        public string UpdatedBy { get; init; } = string.Empty;

        public IntegrationClient ToClient() => new(
            Id, ClientId, DisplayName, Description, [.. Scopes.Order(StringComparer.Ordinal)], IsActive, SecretSetAt, LastTokenAt,
            CreatedAt, CreatedBy, UpdatedAt, UpdatedBy);
    }

    private sealed class SecretRow
    {
        public long Id { get; init; }

        public byte[] SecretHash { get; init; } = [];

        public string[] Scopes { get; init; } = [];

        public bool IsActive { get; init; }
    }

    private sealed class TokenRow
    {
        public long Id { get; init; }

        public string ClientId { get; init; } = string.Empty;

        public string DisplayName { get; init; } = string.Empty;

        public string[] Scopes { get; init; } = [];
    }
}
