using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using Sqm.Application.Integration;
using Sqm.Infrastructure.DataImport;
using Sqm.Infrastructure.Identity;
using Sqm.Infrastructure.Integration;

namespace Sqm.Integration.Tests;

/// <summary>
/// The integration API's PostgreSQL stores against the real database (migration 025, ADR-024), connected as
/// the application role, so that the grants are tested with them: what sqm_app may not do, it cannot do.
/// Skips with the reason when there is no database or the migration is not applied.
/// </summary>
public sealed class IntegrationStoreTests : IAsyncLifetime
{
    private const string Prefix = "itest-";
    private static readonly string Owner = ImportQueueFixture.ConnectionString;
    private static readonly string App = IdentityFixture.ConnectionString;

    private readonly string _run = Guid.NewGuid().ToString("N")[..10];
    private IdentityDataSource? _db;
    private string? _unavailable;

    public async ValueTask InitializeAsync()
    {
        try
        {
            await using var probe = new NpgsqlConnection(App);
            await probe.OpenAsync();
            if (await probe.ExecuteScalarAsync<string?>("SELECT to_regclass('integration.outbox_event')::text") is null)
            {
                _unavailable = "migration 025 is not applied on this database";
                return;
            }

            _db = new IdentityDataSource(Options.Create(new PostgresOptions { ConnectionString = App }));
        }
        catch (NpgsqlException ex)
        {
            _unavailable = $"no PostgreSQL at the test connection: {ex.Message}";
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_db is null)
        {
            return;
        }

        // The application role may not delete a client or a declared event, by design; the owner cleans up.
        await using var owner = new NpgsqlConnection(Owner);
        await owner.OpenAsync();
        await owner.ExecuteAsync("""
            DELETE FROM integration.declared_event WHERE client_pk IN (SELECT id FROM integration.client WHERE client_id LIKE @p);
            DELETE FROM integration.client WHERE client_id LIKE @p;
            DELETE FROM integration.outbox_event WHERE type LIKE 'itest.%';
            """, new { p = $"{Prefix}{_run}%" });
        await _db.DisposeAsync();
    }

    private PostgresIntegrationClientStore Clients(TimeSpan? lifetime = null) => new(
        _db!, Options.Create(new IntegrationOptions { TokenLifetime = lifetime ?? TimeSpan.FromMinutes(15) }), TimeProvider.System);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void SkipIfUnavailable()
    {
        if (_unavailable is not null)
        {
            Assert.Skip(_unavailable);
        }
    }

    [Fact]
    public async Task A_client_gets_a_token_carrying_the_scopes_it_asks_for_and_only_with_its_own_secret()
    {
        SkipIfUnavailable();
        var store = Clients();
        var issued = await store.CreateAsync(
            new NewIntegrationClient($"{Prefix}{_run}-a", "Test A", "", [IntegrationScopes.InventoryRead, IntegrationScopes.EventsRead]), "test", Ct);

        Assert.NotNull(issued);
        var all = await store.GrantAsync(issued.Client.ClientId, issued.Secret, null, Ct);
        var narrow = await store.GrantAsync(issued.Client.ClientId, issued.Secret, new HashSet<string> { IntegrationScopes.EventsRead }, Ct);
        var wrong = await store.GrantAsync(issued.Client.ClientId, issued.Secret + "x", null, Ct);
        var notHeld = await store.GrantAsync(issued.Client.ClientId, issued.Secret, new HashSet<string> { IntegrationScopes.EventsWrite }, Ct);

        Assert.Equal(TokenGrantOutcome.Issued, all.Outcome);
        var resolved = await store.ResolveAsync(all.AccessToken!, Ct);
        Assert.Equal(new[] { IntegrationScopes.EventsRead, IntegrationScopes.InventoryRead }, resolved!.Scopes.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(new[] { IntegrationScopes.EventsRead }, (await store.ResolveAsync(narrow.AccessToken!, Ct))!.Scopes.ToArray());
        Assert.Equal((TokenGrantOutcome.InvalidClient, TokenGrantOutcome.InvalidScope), (wrong.Outcome, notHeld.Outcome));
        Assert.Null(await store.ResolveAsync(IntegrationCredentials.NewValue(), Ct));
    }

    [Fact]
    public async Task Only_hashes_are_stored_for_a_secret_and_a_token()
    {
        SkipIfUnavailable();
        var store = Clients();
        var issued = (await store.CreateAsync(new NewIntegrationClient($"{Prefix}{_run}-h", "Test H", "", [IntegrationScopes.InventoryRead]), "test", Ct))!;
        var token = (await store.GrantAsync(issued.Client.ClientId, issued.Secret, null, Ct)).AccessToken!;

        await using var connection = new NpgsqlConnection(App);
        var secretHash = await connection.ExecuteScalarAsync<byte[]>(
            "SELECT secret_hash FROM integration.client WHERE id = @id", new { id = issued.Client.Id });
        var tokenRows = await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM integration.access_token WHERE token_hash = @h", new { h = IntegrationCredentials.Hash(token) });
        var anyPlain = await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM integration.access_token WHERE encode(token_hash, 'escape') LIKE '%' || @t || '%'", new { t = token });

        Assert.Equal(IntegrationCredentials.Hash(issued.Secret), secretHash);
        Assert.Equal((1L, 0L), (tokenRows, anyPlain));
    }

    [Fact]
    public async Task Rotating_the_secret_or_deactivating_the_client_ends_its_tokens_at_once()
    {
        SkipIfUnavailable();
        var store = Clients();
        var issued = (await store.CreateAsync(new NewIntegrationClient($"{Prefix}{_run}-r", "Test R", "", [IntegrationScopes.InventoryRead]), "test", Ct))!;
        var before = (await store.GrantAsync(issued.Client.ClientId, issued.Secret, null, Ct)).AccessToken!;

        var rotated = (await store.RotateSecretAsync(issued.Client.Id, "test", Ct))!;
        Assert.Null(await store.ResolveAsync(before, Ct));
        Assert.Equal(TokenGrantOutcome.InvalidClient, (await store.GrantAsync(issued.Client.ClientId, issued.Secret, null, Ct)).Outcome);

        var after = (await store.GrantAsync(issued.Client.ClientId, rotated.Secret, null, Ct)).AccessToken!;
        Assert.NotNull(await store.ResolveAsync(after, Ct));

        await store.UpdateAsync(issued.Client.Id, new IntegrationClientChange("Test R", "", [IntegrationScopes.InventoryRead], false), "test", Ct);
        Assert.Null(await store.ResolveAsync(after, Ct));
        Assert.Equal(TokenGrantOutcome.InvalidClient, (await store.GrantAsync(issued.Client.ClientId, rotated.Secret, null, Ct)).Outcome);
    }

    [Fact]
    public async Task An_expired_token_is_refused_and_swept()
    {
        SkipIfUnavailable();
        var store = Clients(TimeSpan.FromSeconds(-5));
        var issued = (await store.CreateAsync(new NewIntegrationClient($"{Prefix}{_run}-e", "Test E", "", [IntegrationScopes.InventoryRead]), "test", Ct))!;
        var token = (await store.GrantAsync(issued.Client.ClientId, issued.Secret, null, Ct)).AccessToken!;

        Assert.Null(await store.ResolveAsync(token, Ct));
        Assert.True(await store.DeleteExpiredTokensAsync(Ct) >= 1);
    }

    [Fact]
    public async Task A_declared_event_is_kept_once_and_the_application_cannot_rewrite_history()
    {
        SkipIfUnavailable();
        var client = (await Clients().CreateAsync(new NewIntegrationClient($"{Prefix}{_run}-d", "Test D", "", [IntegrationScopes.EventsWrite]), "test", Ct))!;
        var events = new PostgresDeclaredEventStore(_db!);
        var swap = new DeclaredEvent(
            $"crm-{_run}", "crm", DeclaredEventType.SimSwap, DateTimeOffset.UtcNow.AddMinutes(-3), "9121234567",
            "432110123456789", null, "432110987654321", null, "ORD-1");

        var first = await events.RecordAsync(client.Client.Id, swap, Ct);
        var retry = await events.RecordAsync(client.Client.Id, swap, Ct);
        var reused = await events.RecordAsync(client.Client.Id, swap with { Msisdn = "9129999999" }, Ct);
        var byOldSim = await events.ListForAsync(null, "432110987654321", null, 10, Ct);

        Assert.Equal((DeclaredEventOutcome.Accepted, DeclaredEventOutcome.Duplicate, DeclaredEventOutcome.Conflict), (first, retry, reused));
        Assert.Contains(byOldSim, e => e.Event.EventId == swap.EventId && e.Event.Type == DeclaredEventType.SimSwap);

        await using var connection = new NpgsqlConnection(App);
        var refused = await Assert.ThrowsAsync<PostgresException>(() =>
            connection.ExecuteAsync("DELETE FROM integration.declared_event WHERE event_id = @id", new { id = swap.EventId }));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, refused.SqlState);
    }

    [Fact]
    public async Task The_feed_is_read_in_sequence_and_an_event_exists_only_if_its_transaction_committed()
    {
        SkipIfUnavailable();
        var outbox = new PostgresIntegrationOutbox(_db!);
        var start = (await outbox.ReadAsync(long.MaxValue - 1, 1, Ct)).Latest;

        var one = await outbox.AppendAsync("itest.one", _run, new { n = 1 }, Ct);
        await using (var connection = new NpgsqlConnection(App))
        {
            await connection.OpenAsync(Ct);
            await using var transaction = await connection.BeginTransactionAsync(Ct);
            await IntegrationOutboxSql.AppendAsync(connection, transaction, "itest.rolled-back", _run, "{}", Ct);
            await transaction.RollbackAsync(Ct);
        }

        var two = await outbox.AppendAsync("itest.two", _run, new { n = 2 }, Ct);
        var page = await outbox.ReadAsync(start, 100, Ct);
        var mine = page.Events.Where(e => e.Subject == _run).ToList();

        Assert.Equal(new[] { "itest.one", "itest.two" }, mine.Select(e => e.Type).ToArray());
        Assert.True(two.Sequence > one.Sequence);
        Assert.Equal(2, mine[1].Payload.GetProperty("n").GetInt32());
        Assert.True(page.Latest >= two.Sequence);
    }
}
