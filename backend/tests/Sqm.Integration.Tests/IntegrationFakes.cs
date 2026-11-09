using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sqm.Application.Integration;

namespace Sqm.Integration.Tests;

/// <summary>
/// The integration API's stores in memory, for endpoint tests that need no database. They use the real
/// hashing and the real event rules, so a test of an endpoint exercises those too.
/// </summary>
public sealed class InMemoryIntegrationClients : IIntegrationClientStore
{
    private readonly object _gate = new();
    private readonly List<(IntegrationClient Client, byte[] Hash)> _clients = [];
    private readonly List<(byte[] Hash, long Client, IReadOnlySet<string> Scopes, DateTimeOffset Expires)> _tokens = [];

    /// <summary>How long a token lives here.</summary>
    public TimeSpan Lifetime { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Registers a client with a known secret; returns its row key.</summary>
    public long Add(string clientId, string secret, bool active, params string[] scopes)
    {
        lock (_gate)
        {
            var id = _clients.Count + 1;
            _clients.Add((new IntegrationClient(
                id, clientId, clientId, string.Empty, [.. scopes], active, DateTimeOffset.UtcNow, null, DateTimeOffset.UtcNow, "test",
                DateTimeOffset.UtcNow, "test"), IntegrationCredentials.Hash(secret)));
            return id;
        }
    }

    /// <summary>A token issued directly, as the grant would, for tests of the routes behind it.</summary>
    public string Issue(long client, params string[] scopes)
    {
        var token = IntegrationCredentials.NewValue();
        lock (_gate)
        {
            _tokens.Add((IntegrationCredentials.Hash(token), client, scopes.ToHashSet(StringComparer.Ordinal), DateTimeOffset.UtcNow + Lifetime));
        }

        return token;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<IntegrationClient>> ListAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<IntegrationClient>>([.. _clients.Select(c => c.Client)]);
        }
    }

    /// <inheritdoc />
    public Task<IntegrationClient?> GetAsync(long id, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult(_clients.Select(c => c.Client).FirstOrDefault(c => c.Id == id));
        }
    }

    /// <inheritdoc />
    public Task<IssuedSecret?> CreateAsync(NewIntegrationClient client, string actor, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_clients.Any(c => c.Client.ClientId == client.ClientId))
            {
                return Task.FromResult<IssuedSecret?>(null);
            }

            var secret = IntegrationCredentials.NewValue();
            var id = Add(client.ClientId, secret, true, [.. client.Scopes]);
            var stored = _clients[(int)id - 1].Client with { DisplayName = client.DisplayName, Description = client.Description, CreatedBy = actor };
            _clients[(int)id - 1] = (stored, _clients[(int)id - 1].Hash);
            return Task.FromResult<IssuedSecret?>(new IssuedSecret(stored, secret));
        }
    }

    /// <inheritdoc />
    public Task<IntegrationClient?> UpdateAsync(long id, IntegrationClientChange change, string actor, CancellationToken ct)
    {
        lock (_gate)
        {
            var index = _clients.FindIndex(c => c.Client.Id == id);
            if (index < 0)
            {
                return Task.FromResult<IntegrationClient?>(null);
            }

            var updated = _clients[index].Client with
            {
                DisplayName = change.DisplayName, Description = change.Description, Scopes = change.Scopes, IsActive = change.IsActive,
                UpdatedBy = actor,
            };
            _clients[index] = (updated, _clients[index].Hash);
            if (!change.IsActive)
            {
                _tokens.RemoveAll(t => t.Client == id);
            }

            return Task.FromResult<IntegrationClient?>(updated);
        }
    }

    /// <inheritdoc />
    public Task<IssuedSecret?> RotateSecretAsync(long id, string actor, CancellationToken ct)
    {
        lock (_gate)
        {
            var index = _clients.FindIndex(c => c.Client.Id == id);
            if (index < 0)
            {
                return Task.FromResult<IssuedSecret?>(null);
            }

            var secret = IntegrationCredentials.NewValue();
            _clients[index] = (_clients[index].Client, IntegrationCredentials.Hash(secret));
            _tokens.RemoveAll(t => t.Client == id);
            return Task.FromResult<IssuedSecret?>(new IssuedSecret(_clients[index].Client, secret));
        }
    }

    /// <inheritdoc />
    public Task<TokenGrant> GrantAsync(string clientId, string secret, IReadOnlySet<string>? requested, CancellationToken ct)
    {
        var none = new HashSet<string>(StringComparer.Ordinal);
        lock (_gate)
        {
            var found = _clients.FirstOrDefault(c => c.Client.ClientId == clientId);
            if (found.Client is null || !IntegrationCredentials.Matches(secret, found.Hash) || !found.Client.IsActive)
            {
                return Task.FromResult(new TokenGrant(TokenGrantOutcome.InvalidClient, null, none, null, found.Client?.Id));
            }

            var held = found.Client.Scopes.ToHashSet(StringComparer.Ordinal);
            var scopes = requested is { Count: > 0 } ? requested : held;
            if (!scopes.All(held.Contains))
            {
                return Task.FromResult(new TokenGrant(TokenGrantOutcome.InvalidScope, null, none, null, found.Client.Id));
            }

            var token = Issue(found.Client.Id, [.. scopes]);
            return Task.FromResult(new TokenGrant(TokenGrantOutcome.Issued, token, scopes, DateTimeOffset.UtcNow + Lifetime, found.Client.Id));
        }
    }

    /// <inheritdoc />
    public Task<AuthenticatedClient?> ResolveAsync(string token, CancellationToken ct)
    {
        lock (_gate)
        {
            var hash = IntegrationCredentials.Hash(token);
            var found = _tokens.FirstOrDefault(t => t.Hash.AsSpan().SequenceEqual(hash) && t.Expires > DateTimeOffset.UtcNow);
            var client = found.Hash is null ? null : _clients.Select(c => c.Client).FirstOrDefault(c => c.Id == found.Client && c.IsActive);
            return Task.FromResult(client is null ? null : new AuthenticatedClient(client.Id, client.ClientId, client.DisplayName, found.Scopes));
        }
    }

    /// <inheritdoc />
    public Task<int> DeleteExpiredTokensAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult(_tokens.RemoveAll(t => t.Expires <= DateTimeOffset.UtcNow));
        }
    }
}

/// <summary>Declared events in memory, once per client and id, as the database stores them.</summary>
public sealed class InMemoryDeclaredEvents : IDeclaredEventStore
{
    private readonly object _gate = new();
    private readonly List<(long Client, StoredDeclaredEvent Stored, byte[] Hash)> _events = [];

    /// <summary>How many are stored.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _events.Count;
            }
        }
    }

    /// <inheritdoc />
    public Task<DeclaredEventOutcome> RecordAsync(long clientPk, DeclaredEvent declared, CancellationToken ct)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(DeclaredEventRules.Canonical(declared)));
        lock (_gate)
        {
            var existing = _events.FirstOrDefault(e => e.Client == clientPk && e.Stored.Event.EventId == declared.EventId);
            if (existing.Stored is not null)
            {
                return Task.FromResult(existing.Hash.AsSpan().SequenceEqual(hash) ? DeclaredEventOutcome.Duplicate : DeclaredEventOutcome.Conflict);
            }

            _events.Add((clientPk, new StoredDeclaredEvent(_events.Count + 1, "lodestone", declared, DateTimeOffset.UtcNow), hash));
            return Task.FromResult(DeclaredEventOutcome.Accepted);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<StoredDeclaredEvent>> ListForAsync(string? msisdn, string? imsi, string? iccid, int limit, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<StoredDeclaredEvent>>([.. _events.Select(e => e.Stored)
                .Where(s => (msisdn is not null && s.Event.Msisdn == msisdn)
                            || (imsi is not null && (s.Event.Imsi == imsi || s.Event.PreviousImsi == imsi))
                            || (iccid is not null && (s.Event.Iccid == iccid || s.Event.PreviousIccid == iccid)))
                .OrderByDescending(s => s.Event.OccurredAt)
                .Take(limit)]);
        }
    }
}

/// <summary>The event feed in memory, in sequence order.</summary>
public sealed class InMemoryOutbox : IIntegrationOutbox
{
    private readonly object _gate = new();
    private readonly List<OutboxEvent> _events = [];

    /// <inheritdoc />
    public Task<OutboxEvent> AppendAsync(string type, string subject, object payload, CancellationToken ct)
    {
        lock (_gate)
        {
            var appended = new OutboxEvent(
                _events.Count + 1, Guid.NewGuid(), type, subject, DateTimeOffset.UtcNow, JsonSerializer.SerializeToElement(payload));
            _events.Add(appended);
            return Task.FromResult(appended);
        }
    }

    /// <inheritdoc />
    public Task<OutboxPage> ReadAsync(long after, int limit, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult(new OutboxPage(
                [.. _events.Where(e => e.Sequence > after).Take(limit)],
                _events.Count == 0 ? null : _events[0].Sequence,
                _events.Count == 0 ? 0 : _events[^1].Sequence));
        }
    }

    /// <inheritdoc />
    public Task<int> PruneAsync(DateTimeOffset before, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult(_events.RemoveAll(e => e.OccurredAt < before));
        }
    }
}
