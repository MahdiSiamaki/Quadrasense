using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Sqm.Application.Identity;

namespace Sqm.Integration.Tests;

/// <summary>
/// The Persian captcha over HTTP, on the real pipeline (ADR-026; MCI red line §2-3 row 3): hidden at
/// first, asked for after three failed sign-ins, checked before the password so that it never locks an
/// account, and after five failed recovery codes. Each test signs in from an address of its own.
/// </summary>
[Collection("Captcha")]
public sealed class CaptchaEndpointTests
    : IClassFixture<IdentityFixture>, IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
{
    private const string Password = "Vx9-quiet-owl-lantern";

    private readonly IdentityFixture _fixture;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly RecordingRenderer _renderer = new();
    private readonly List<long> _users = [];

    public CaptchaEndpointTests(IdentityFixture fixture, WebApplicationFactory<Program> factory)
    {
        _fixture = fixture;
        _factory = factory;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var id in _users)
        {
            await _fixture.DeleteUserAsync(id);
        }

        _renderer.Dispose();
    }

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

    /// <summary>A host whose every request comes from <paramref name="address"/>, drawing with the recording renderer.</summary>
    private WebApplicationFactory<Program> Host(IPAddress address) =>
        _factory.WithWebHostBuilder(host => host.ConfigureTestServices(services =>
        {
            services.AddSingleton<ICaptchaRenderer>(_renderer);
            services.AddSingleton<IStartupFilter>(new FixedAddress(address));

            // Every channel on, so recovery's own checks pass and the captcha is what answers.
            services.AddSingleton<IContactDelivery>(new CapturingDelivery());
        }));

    private static IPAddress NewAddress() =>
        new([10, (byte)Random.Shared.Next(256), (byte)Random.Shared.Next(256), (byte)Random.Shared.Next(1, 255)]);

    private async Task<string> CreateAsync()
    {
        var username = IdentityFixture.UniqueUsername("captcha");
        var id = await _fixture.Users.CreateAsync(
            new NewUser(username, "Test Person", null, null, null, ["viewer"], MustChangePassword: false),
            Password, "tests", IdentityFixture.Context, Ct);
        _users.Add(id);
        return username;
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> SignInAsync(
        HttpClient client, string username, string password, string? captchaId = null, string? captchaAnswer = null)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/v1/auth/login", new { username, password, captchaId, captchaAnswer }, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private static async Task<string> PictureAsync(HttpClient client)
    {
        var picture = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/captcha", Ct);
        return picture.GetProperty("id").GetString()!;
    }

    // ======================================================================= sign-in

    [Fact]
    public async Task Hidden_at_first_and_asked_for_after_three_failed_sign_ins_even_with_the_right_password()
    {
        await SkipWithoutTablesAsync();
        var username = await CreateAsync();
        using var host = Host(NewAddress());
        using var client = host.CreateClient();

        for (var i = 1; i <= 3; i++)
        {
            var (status, body) = await SignInAsync(client, username, "wrong-password-" + i);
            Assert.Equal(HttpStatusCode.Unauthorized, status);
            Assert.Equal("InvalidCredentials", body.GetProperty("status").GetString());
            Assert.Equal(i == 3, body.GetProperty("captchaRequired").GetBoolean());
        }

        var (refused, why) = await SignInAsync(client, username, Password);
        Assert.Equal(HttpStatusCode.Unauthorized, refused);
        Assert.Equal("CaptchaRequired", why.GetProperty("status").GetString());

        var id = await PictureAsync(client);
        var (signedIn, _) = await SignInAsync(client, username, Password, id, _renderer.LastText);
        Assert.Equal(HttpStatusCode.OK, signedIn);
    }

    [Fact]
    public async Task A_wrong_answer_is_refused_before_the_password_and_never_locks_the_account()
    {
        await SkipWithoutTablesAsync();
        var username = await CreateAsync();
        using (var first = Host(NewAddress()))
        using (var firstClient = first.CreateClient())
        {
            for (var i = 0; i < 3; i++)
            {
                await SignInAsync(firstClient, username, "wrong-password");
            }
        }

        // From a second address - sign-in allows six attempts a minute from one - where the name's own
        // three failures ask for the captcha. Five more wrong passwords would lock the account, if they
        // reached the password check.
        using var host = Host(NewAddress());
        using var client = host.CreateClient();

        for (var i = 0; i < 5; i++)
        {
            var id = await PictureAsync(client);
            var (status, body) = await SignInAsync(client, username, "wrong-password", id, "نادرست");
            Assert.Equal(HttpStatusCode.Unauthorized, status);
            Assert.Equal("CaptchaRequired", body.GetProperty("status").GetString());
        }

        var answer = await PictureAsync(client);
        var (signedIn, _) = await SignInAsync(client, username, Password, answer, _renderer.LastText);
        Assert.Equal(HttpStatusCode.OK, signedIn);
    }

    [Fact]
    public async Task The_page_is_told_when_its_address_needs_the_captcha_and_another_address_is_not()
    {
        await SkipWithoutTablesAsync();
        var username = await CreateAsync();
        using var host = Host(NewAddress());
        using var client = host.CreateClient();
        using var elsewhere = Host(NewAddress());
        using var otherClient = elsewhere.CreateClient();

        Assert.False((await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/options", Ct)).GetProperty("captchaRequired").GetBoolean());

        for (var i = 0; i < 3; i++)
        {
            await SignInAsync(client, "nobody." + Guid.NewGuid().ToString("N"), "wrong-password");
        }

        Assert.True((await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/options", Ct)).GetProperty("captchaRequired").GetBoolean());
        Assert.False((await otherClient.GetFromJsonAsync<JsonElement>("/api/v1/auth/options", Ct)).GetProperty("captchaRequired").GetBoolean());

        // The person with the right password elsewhere signs in without one.
        var (status, _) = await SignInAsync(otherClient, username, Password);
        Assert.Equal(HttpStatusCode.OK, status);
    }

    [Fact]
    public async Task A_picture_comes_as_an_image_never_cached_with_the_keys_of_its_keyboard()
    {
        await SkipWithoutTablesAsync();
        using var host = Host(NewAddress());
        using var client = host.CreateClient();

        using var response = await client.GetAsync("/api/v1/auth/captcha?theme=dark", Ct);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
        Assert.StartsWith("data:image/png;base64,", body.GetProperty("image").GetString(), StringComparison.Ordinal);
        Assert.Equal(CaptchaText.Alphabet, body.GetProperty("alphabet").GetString());
        Assert.DoesNotContain(_renderer.LastText!, body.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_wrong_answer_is_in_the_audit_trail()
    {
        await SkipWithoutTablesAsync();
        var username = await CreateAsync();
        using var host = Host(NewAddress());
        using var client = host.CreateClient();

        for (var i = 0; i < 3; i++)
        {
            await SignInAsync(client, username, "wrong-password");
        }

        await SignInAsync(client, username, Password, await PictureAsync(client), "نادرست");

        await using var connection = new NpgsqlConnection(IdentityFixture.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM audit.event WHERE action = 'auth.captcha' AND actor_name = @name", connection);
        command.Parameters.AddWithValue("name", username);

        Assert.Equal(1L, await command.ExecuteScalarAsync(Ct));
    }

    // ======================================================================= recovery: five elsewhere

    [Fact]
    public async Task Recovery_asks_for_it_after_five_wrong_codes()
    {
        await SkipWithoutTablesAsync();
        var identifier = "nobody." + Guid.NewGuid().ToString("N");
        using var host = Host(NewAddress());
        using var client = host.CreateClient();

        JsonElement last = default;
        for (var i = 1; i <= 5; i++)
        {
            using var response = await client.PostAsJsonAsync(
                "/api/v1/auth/recovery/complete",
                new { identifier, code = "000000", newPassword = "Zq4-amber-river-stone" }, Ct);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            last = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errors");
            Assert.Equal(i == 5, last.TryGetProperty("captcha", out _));
        }

        using var asked = await client.PostAsJsonAsync(
            "/api/v1/auth/recovery/start", new { identifier, channel = "Email" }, Ct);
        var errors = (await asked.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errors");
        Assert.True(errors.TryGetProperty("captcha", out _));
    }

    private sealed class FixedAddress(IPAddress address) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = address;
                return nextMiddleware(context);
            });
            next(app);
        };
    }
}
