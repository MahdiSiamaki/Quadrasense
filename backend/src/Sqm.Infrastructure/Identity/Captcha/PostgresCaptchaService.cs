using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.Extensions.Options;
using Sqm.Application.Identity;

namespace Sqm.Infrastructure.Identity.Captcha;

/// <summary>
/// The Persian captcha's state, in PostgreSQL so that any replica of the API can check a picture another
/// one issued (ADR-026).
/// </summary>
/// <remarks>
/// <para>
/// <b>When it is asked for.</b> Each failure is counted against two subjects - the address it came from
/// and the name it tried - and a captcha is needed once either has failed often enough within the
/// window. Counting the name stops one account being tried from many addresses; counting the address
/// stops many names being tried from one. A name with no account counts exactly as one with an account,
/// so the question "is a captcha needed?" tells nobody which accounts exist.
/// </para>
/// <para>
/// <b>What is kept.</b> Hashes only. A challenge is found by the hash of its id and holds the hash of its
/// answer bound to that id. A subject is the hash of the address or of the name - not the name, because
/// what people type into a username box is sometimes their password - and it is kept only as long as
/// the window: each new failure removes those that have stopped counting.
/// </para>
/// </remarks>
public sealed class PostgresCaptchaService : ICaptchaService
{
    /// <summary>Longer than any id this service issues: anything longer is not one of ours.</summary>
    private const int MaxIdLength = 64;

    private readonly IdentityDataSource _db;
    private readonly ICaptchaRenderer _renderer;
    private readonly CaptchaOptions _options;
    private readonly TimeProvider _clock;

    /// <summary>Creates the service.</summary>
    public PostgresCaptchaService(
        IdentityDataSource db, ICaptchaRenderer renderer, IOptions<AuthOptions> auth, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(auth);

        _db = db;
        _renderer = renderer;
        _options = auth.Value.Captcha;
        _clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<bool> IsRequiredAsync(
        CaptchaScope scope, string? address, string? username, CancellationToken ct)
    {
        if (!_options.Enabled)
        {
            return false;
        }

        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);

        return await connection.ExecuteScalarAsync<bool>(_db.Command("""
            SELECT EXISTS (
                SELECT 1
                FROM auth.sign_in_failure
                WHERE scope = @scope AND subject = ANY(@subjects) AND failed_at > @since
                GROUP BY subject
                HAVING count(*) >= @threshold)
            """,
            new
            {
                scope = Name(scope),
                subjects = Subjects(address, username),
                since = _clock.GetUtcNow() - _options.FailureWindow,
                threshold = Threshold(scope),
            }, ct)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RecordFailureAsync(
        CaptchaScope scope, string? address, string? username, CancellationToken ct)
    {
        if (!_options.Enabled)
        {
            return;
        }

        var now = _clock.GetUtcNow();
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);

        // The ones that stopped counting go in the same statement, so a hashed name lives no longer
        // than the window whatever the sweeper's schedule.
        await connection.ExecuteAsync(_db.Command("""
            WITH lapsed AS (
                DELETE FROM auth.sign_in_failure WHERE failed_at <= @since
            )
            INSERT INTO auth.sign_in_failure (scope, subject, failed_at)
            SELECT @scope, subject, @now FROM unnest(@subjects) AS subject
            """,
            new
            {
                scope = Name(scope),
                subjects = Subjects(address, username),
                since = now - _options.FailureWindow,
                now,
            }, ct)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ForgetAsync(CaptchaScope scope, string? username, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return;
        }

        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);

        await connection.ExecuteAsync(_db.Command(
            "DELETE FROM auth.sign_in_failure WHERE scope = @scope AND subject = @subject",
            new { scope = Name(scope), subject = NameSubject(username) }, ct)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<CaptchaChallenge> IssueAsync(CaptchaTheme theme, CancellationToken ct)
    {
        var text = CaptchaText.Generate();
        var id = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(18));
        var expires = _clock.GetUtcNow() + _options.ChallengeLifetime;

        // The characters come from the cryptographic generator; only the drawing's jitter comes from
        // this one, seeded by it, which leaves nothing to predict about the answer.
#pragma warning disable CA5394 // The drawing's variety is not a secret; the text is drawn from RandomNumberGenerator.
        var random = new Random(RandomNumberGenerator.GetInt32(int.MaxValue));
#pragma warning restore CA5394
        var png = _renderer.Render(text, theme, RandomNumberGenerator.GetInt32(_renderer.PatternCount), random);

        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);

        await connection.ExecuteAsync(_db.Command("""
            INSERT INTO auth.captcha_challenge (id_sha256, answer_sha256, expires_at)
            VALUES (@idHash, @answerHash, @expires)
            """,
            new { idHash = IdHash(id), answerHash = CaptchaText.AnswerHash(id, text), expires },
            ct)).ConfigureAwait(false);

        return new CaptchaChallenge(id, png, expires);
    }

    /// <inheritdoc />
    public async Task<bool> RedeemAsync(string? id, string? answer, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(id) || id.Length > MaxIdLength)
        {
            return false;
        }

        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);

        // Deleted whatever the answer: a picture gets one guess.
        var challenge = await connection.QuerySingleOrDefaultAsync<ChallengeRow>(_db.Command("""
            DELETE FROM auth.captcha_challenge
            WHERE id_sha256 = @idHash
            RETURNING answer_sha256 AS AnswerSha256, expires_at AS ExpiresAt
            """,
            new { idHash = IdHash(id) }, ct)).ConfigureAwait(false);

        return challenge is not null
            && challenge.ExpiresAt > _clock.GetUtcNow()
            && CryptographicOperations.FixedTimeEquals(challenge.AnswerSha256, CaptchaText.AnswerHash(id, answer));
    }

    /// <inheritdoc />
    public async Task<int> SweepAsync(CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);

        var challenges = await connection.ExecuteAsync(_db.Command(
            "DELETE FROM auth.captcha_challenge WHERE expires_at <= @now",
            new { now }, ct)).ConfigureAwait(false);

        var failures = await connection.ExecuteAsync(_db.Command(
            "DELETE FROM auth.sign_in_failure WHERE failed_at <= @since",
            new { since = now - _options.FailureWindow }, ct)).ConfigureAwait(false);

        return challenges + failures;
    }

    private int Threshold(CaptchaScope scope) =>
        scope == CaptchaScope.Login ? _options.LoginFailuresBefore : _options.RecoveryFailuresBefore;

    private static string Name(CaptchaScope scope) => scope == CaptchaScope.Login ? "login" : "recovery";

    /// <summary>
    /// The address and the name, each when known: the two things a failure counts against. Kestrel always
    /// knows the address; an unknown one is not made a subject, because a shared "unknown" would let
    /// strangers' failures ask the captcha of each other.
    /// </summary>
    private static byte[][] Subjects(string? address, string? username)
    {
        var subjects = new List<byte[]>(2);

        if (!string.IsNullOrWhiteSpace(address))
        {
            subjects.Add(Hash("address:" + address));
        }

        if (!string.IsNullOrWhiteSpace(username))
        {
            subjects.Add(NameSubject(username));
        }

        return [.. subjects];
    }

    /// <summary>Names are compared as the sign-in compares them: trimmed, case ignored.</summary>
    private static byte[] NameSubject(string username) => Hash("name:" + username.Trim().ToUpperInvariant());

    private static byte[] IdHash(string id) => Hash("captcha:" + id);

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));

    private sealed record ChallengeRow(byte[] AnswerSha256, DateTimeOffset ExpiresAt);
}
