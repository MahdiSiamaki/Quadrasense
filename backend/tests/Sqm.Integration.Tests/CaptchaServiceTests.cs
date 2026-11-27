using System.Text;
using Npgsql;
using Sqm.Application.Identity;
using Sqm.Infrastructure.DataImport;
using Sqm.Infrastructure.Identity;
using Sqm.Infrastructure.Identity.Captcha;

namespace Sqm.Integration.Tests;

/// <summary>
/// The captcha's state in PostgreSQL (ADR-026): one answer per picture, ten minutes; asked for after three
/// failed sign-ins or five failed recoveries, counted by address and by name; and nothing kept that can be
/// read back. Each test uses addresses and names of its own, so the shared development database cannot
/// carry one test's failures into another. One collection with the endpoint tests, so the sweep - which
/// takes every lapsed row - never runs beside them.
/// </summary>
[Collection("Captcha")]
public sealed class CaptchaServiceTests : IClassFixture<IdentityFixture>, IDisposable
{
    private readonly IdentityFixture _fixture;
    private readonly RecordingRenderer _renderer = new();
    private readonly MovableClock _clock = new();
    private readonly IdentityDataSource _db;
    private readonly PostgresCaptchaService _captcha;

    public CaptchaServiceTests(IdentityFixture fixture)
    {
        _fixture = fixture;
        _db = new IdentityDataSource(Microsoft.Extensions.Options.Options.Create(
            new PostgresOptions { ConnectionString = IdentityFixture.ConnectionString }));
        _captcha = new PostgresCaptchaService(
            _db, _renderer, Microsoft.Extensions.Options.Options.Create(new AuthOptions()), _clock);
    }

    public void Dispose()
    {
        _renderer.Dispose();
        _db.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Address() => $"10.{Random.Shared.Next(256)}.{Random.Shared.Next(256)}.{Random.Shared.Next(1, 255)}#{Guid.NewGuid():N}";

    private static string Name() => IdentityFixture.UniqueUsername("captcha");

    private async Task SkipWithoutTablesAsync()
    {
        if (!_fixture.IsAvailable)
        {
            Assert.Skip(_fixture.UnavailableReason ?? "no database");
        }

        await using var connection = new NpgsqlConnection(IdentityFixture.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "SELECT 1 FROM information_schema.tables WHERE table_schema = 'auth' AND table_name = 'captcha_challenge'",
            connection);

        if (await command.ExecuteScalarAsync(Ct) is null)
        {
            Assert.Skip("migration 027 (the captcha) is not applied; run the migrator first");
        }
    }

    private async Task FailAsync(CaptchaScope scope, string? address, string? name, int times)
    {
        for (var i = 0; i < times; i++)
        {
            await _captcha.RecordFailureAsync(scope, address, name, Ct);
        }
    }

    // ======================================================================= one answer per picture

    [Fact]
    public async Task The_right_answer_passes_once()
    {
        await SkipWithoutTablesAsync();

        var challenge = await _captcha.IssueAsync(CaptchaTheme.Light, Ct);

        Assert.True(await _captcha.RedeemAsync(challenge.Id, _renderer.LastText, Ct));
        Assert.False(await _captcha.RedeemAsync(challenge.Id, _renderer.LastText, Ct));
    }

    [Fact]
    public async Task A_wrong_answer_uses_the_picture_up()
    {
        await SkipWithoutTablesAsync();

        var challenge = await _captcha.IssueAsync(CaptchaTheme.Light, Ct);

        Assert.False(await _captcha.RedeemAsync(challenge.Id, "نادرست", Ct));
        Assert.False(await _captcha.RedeemAsync(challenge.Id, _renderer.LastText, Ct));
    }

    [Fact]
    public async Task An_answer_with_latin_digits_is_the_same_answer()
    {
        await SkipWithoutTablesAsync();

        var challenge = await _captcha.IssueAsync(CaptchaTheme.Dark, Ct);
        var latin = new string(_renderer.LastText!.Select(c => c is >= '۰' and <= '۹' ? (char)('0' + (c - '۰')) : c).ToArray());

        Assert.True(await _captcha.RedeemAsync(challenge.Id, latin, Ct));
    }

    [Fact]
    public async Task A_picture_lapses_after_ten_minutes()
    {
        await SkipWithoutTablesAsync();

        var challenge = await _captcha.IssueAsync(CaptchaTheme.Light, Ct);
        Assert.InRange(challenge.ExpiresAt - _clock.GetUtcNow(), TimeSpan.FromSeconds(595), TimeSpan.FromSeconds(600));

        _clock.Advance(TimeSpan.FromMinutes(11));

        Assert.False(await _captcha.RedeemAsync(challenge.Id, _renderer.LastText, Ct));
    }

    [Fact]
    public async Task An_id_that_was_never_issued_passes_nothing()
    {
        await SkipWithoutTablesAsync();

        Assert.False(await _captcha.RedeemAsync("never-issued", "ب۳ک۷م", Ct));
        Assert.False(await _captcha.RedeemAsync(null, "ب۳ک۷م", Ct));
        Assert.False(await _captcha.RedeemAsync(new string('x', 500), "ب۳ک۷م", Ct));
    }

    // ======================================================================= when it is asked for

    [Fact]
    public async Task Three_failed_sign_ins_from_an_address_ask_for_it_and_two_do_not()
    {
        await SkipWithoutTablesAsync();
        var address = Address();

        await FailAsync(CaptchaScope.Login, address, Name(), 2);
        Assert.False(await _captcha.IsRequiredAsync(CaptchaScope.Login, address, Name(), Ct));

        await FailAsync(CaptchaScope.Login, address, Name(), 1);
        Assert.True(await _captcha.IsRequiredAsync(CaptchaScope.Login, address, Name(), Ct));
        Assert.True(await _captcha.IsRequiredAsync(CaptchaScope.Login, address, username: null, Ct));
    }

    [Fact]
    public async Task Three_failures_for_a_name_ask_for_it_from_any_address()
    {
        await SkipWithoutTablesAsync();
        var name = Name();

        await FailAsync(CaptchaScope.Login, Address(), name, 1);
        await FailAsync(CaptchaScope.Login, Address(), name, 1);
        await FailAsync(CaptchaScope.Login, Address(), name, 1);

        Assert.True(await _captcha.IsRequiredAsync(CaptchaScope.Login, Address(), name, Ct));
        Assert.False(await _captcha.IsRequiredAsync(CaptchaScope.Login, Address(), Name(), Ct));
    }

    [Fact]
    public async Task A_name_is_one_name_whatever_its_case_and_spaces()
    {
        await SkipWithoutTablesAsync();
        var name = Name();

        await FailAsync(CaptchaScope.Login, Address(), name.ToUpperInvariant(), 2);
        await FailAsync(CaptchaScope.Login, Address(), "  " + name + " ", 1);

        Assert.True(await _captcha.IsRequiredAsync(CaptchaScope.Login, Address(), name, Ct));
    }

    [Fact]
    public async Task Recovery_asks_after_five_and_counts_apart_from_sign_in()
    {
        await SkipWithoutTablesAsync();
        var address = Address();

        await FailAsync(CaptchaScope.Recovery, address, null, 4);
        Assert.False(await _captcha.IsRequiredAsync(CaptchaScope.Recovery, address, null, Ct));
        Assert.False(await _captcha.IsRequiredAsync(CaptchaScope.Login, address, null, Ct));

        await FailAsync(CaptchaScope.Recovery, address, null, 1);
        Assert.True(await _captcha.IsRequiredAsync(CaptchaScope.Recovery, address, null, Ct));
        Assert.False(await _captcha.IsRequiredAsync(CaptchaScope.Login, address, null, Ct));
    }

    [Fact]
    public async Task Failures_stop_counting_after_fifteen_minutes()
    {
        await SkipWithoutTablesAsync();
        var address = Address();

        await FailAsync(CaptchaScope.Login, address, null, 3);
        Assert.True(await _captcha.IsRequiredAsync(CaptchaScope.Login, address, null, Ct));

        _clock.Advance(TimeSpan.FromMinutes(16));

        Assert.False(await _captcha.IsRequiredAsync(CaptchaScope.Login, address, null, Ct));
    }

    [Fact]
    public async Task Proving_the_password_forgets_the_name_and_not_the_address()
    {
        await SkipWithoutTablesAsync();
        var address = Address();
        var name = Name();

        await FailAsync(CaptchaScope.Login, address, name, 3);
        await _captcha.ForgetAsync(CaptchaScope.Login, name, Ct);

        Assert.True(await _captcha.IsRequiredAsync(CaptchaScope.Login, address, null, Ct));
        Assert.False(await _captcha.IsRequiredAsync(CaptchaScope.Login, Address(), name, Ct));
    }

    [Fact]
    public async Task Switched_off_it_is_never_asked_for_and_counts_nothing()
    {
        await SkipWithoutTablesAsync();
        var off = new PostgresCaptchaService(
            _db, _renderer,
            Microsoft.Extensions.Options.Options.Create(new AuthOptions { Captcha = new CaptchaOptions { Enabled = false } }),
            _clock);
        var address = Address();

        for (var i = 0; i < 5; i++)
        {
            await off.RecordFailureAsync(CaptchaScope.Login, address, null, Ct);
        }

        Assert.False(await off.IsRequiredAsync(CaptchaScope.Login, address, null, Ct));
        Assert.False(await _captcha.IsRequiredAsync(CaptchaScope.Login, address, null, Ct));
    }

    // ======================================================================= nothing readable

    [Fact]
    public async Task Neither_a_name_nor_an_address_nor_an_answer_is_stored()
    {
        await SkipWithoutTablesAsync();
        var address = Address();
        var name = Name();

        await FailAsync(CaptchaScope.Login, address, name, 1);
        var challenge = await _captcha.IssueAsync(CaptchaTheme.Light, Ct);

        await using var connection = new NpgsqlConnection(IdentityFixture.ConnectionString);
        await connection.OpenAsync(Ct);

        foreach (var plain in new[] { address, name, challenge.Id, _renderer.LastText! })
        {
            await using var command = new NpgsqlCommand("""
                SELECT count(*) FROM auth.sign_in_failure WHERE position(@plain IN subject) > 0
                UNION ALL
                SELECT count(*) FROM auth.captcha_challenge
                WHERE position(@plain IN id_sha256) > 0 OR position(@plain IN answer_sha256) > 0
                """, connection);
            command.Parameters.AddWithValue("plain", Encoding.UTF8.GetBytes(plain));

            await using var reader = await command.ExecuteReaderAsync(Ct);
            while (await reader.ReadAsync(Ct))
            {
                Assert.Equal(0L, reader.GetInt64(0));
            }
        }
    }

    [Fact]
    public async Task The_sweep_takes_lapsed_pictures_and_failures()
    {
        await SkipWithoutTablesAsync();
        var address = Address();

        var challenge = await _captcha.IssueAsync(CaptchaTheme.Light, Ct);
        await FailAsync(CaptchaScope.Login, address, null, 1);

        _clock.Advance(TimeSpan.FromMinutes(20));
        var swept = await _captcha.SweepAsync(Ct);

        Assert.True(swept >= 2, $"{swept} row(s) swept");
        _clock.Advance(TimeSpan.FromMinutes(-20));
        Assert.False(await _captcha.RedeemAsync(challenge.Id, _renderer.LastText, Ct));
    }
}

/// <summary>The real renderer, keeping the last characters it drew so a test can answer them.</summary>
public sealed class RecordingRenderer : ICaptchaRenderer, IDisposable
{
    private readonly SkiaCaptchaRenderer _inner = new();

    public string? LastText { get; private set; }

    public int PatternCount => _inner.PatternCount;

    public byte[] Render(string text, CaptchaTheme theme, int pattern, Random random)
    {
        LastText = text;
        return _inner.Render(text, theme, pattern, random);
    }

    public void Dispose() => _inner.Dispose();
}
