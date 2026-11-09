using System.Text.Json;
using Dapper;
using Sqm.Application.Identity;

namespace Sqm.Infrastructure.Identity;

/// <summary>Appearance settings in PostgreSQL, one row per person who has changed anything.</summary>
/// <remarks>Not audited: it is how a page looks to the person looking at it, and nothing else.</remarks>
public sealed class PostgresAppearanceStore : IAppearanceStore
{
    private readonly IdentityDataSource _db;

    /// <summary>Creates the store.</summary>
    public PostgresAppearanceStore(IdentityDataSource db) => _db = db;

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, string>?> GetAsync(long userId, CancellationToken ct)
    {
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);

        var json = await connection.ExecuteScalarAsync<string?>(_db.Command(
            "SELECT settings::text FROM auth.user_appearance WHERE user_id = @userId",
            new { userId }, ct)).ConfigureAwait(false);

        return json is null
            ? null
            : JsonSerializer.Deserialize<Dictionary<string, string>>(json);
    }

    /// <inheritdoc />
    public async Task SaveAsync(
        long userId, IReadOnlyDictionary<string, string> settings, CancellationToken ct)
    {
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);

        await connection.ExecuteAsync(_db.Command("""
            INSERT INTO auth.user_appearance (user_id, settings)
            VALUES (@userId, @settings::jsonb)
            ON CONFLICT (user_id) DO UPDATE
            SET settings = EXCLUDED.settings, updated_at = now()
            """,
            new { userId, settings = JsonSerializer.Serialize(settings) }, ct))
            .ConfigureAwait(false);
    }
}
