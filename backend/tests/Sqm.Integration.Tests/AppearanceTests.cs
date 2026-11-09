using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Sqm.Application.Identity;

namespace Sqm.Integration.Tests;

/// <summary>Each person's appearance settings: what is accepted, and whose they are.</summary>
public sealed class AppearanceTests
    : IClassFixture<IdentityFixture>, IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
{
    private const string Password = "Vx9-quiet-owl-lantern";

    private readonly IdentityFixture _fixture;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly List<long> _users = [];
    private readonly List<WebApplicationFactory<Program>> _hosts = [];

    public AppearanceTests(IdentityFixture fixture, WebApplicationFactory<Program> factory)
    {
        _fixture = fixture;
        _factory = factory;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts)
        {
            await host.DisposeAsync();
        }

        foreach (var id in _users)
        {
            await _fixture.DeleteUserAsync(id);
        }
    }

    private void SkipWithoutDatabase()
    {
        if (!_fixture.IsAvailable)
        {
            Assert.Skip(_fixture.UnavailableReason ?? "no database");
        }
    }

    private async Task<(long Id, string Username)> CreateAsync()
    {
        var username = IdentityFixture.UniqueUsername("look");

        var id = await _fixture.Users.CreateAsync(
            new NewUser(username, "Test Person", null, null, null, [], MustChangePassword: false),
            Password, "tests", IdentityFixture.Context, Ct);

        _users.Add(id);
        return (id, username);
    }

    /// <summary>Signs in over the real pipeline and keeps what a browser would.</summary>
    private async Task<(HttpClient Client, string Csrf, JsonElement User)> SignInAsync(string username)
    {
        // A host of its own, so that each sign-in has the login limiter to itself.
        var host = _factory.WithWebHostBuilder(_ => { });
        _hosts.Add(host);

        var client = host.CreateClient(
            new WebApplicationFactoryClientOptions { HandleCookies = false });

        using var login = await client.PostAsJsonAsync(
            "/api/v1/auth/login", new { username, password = Password }, Ct);

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var cookies = login.Headers.GetValues("Set-Cookie")
            .Select(c => c.Split(';')[0])
            .ToList();

        client.DefaultRequestHeaders.Add("Cookie", string.Join("; ", cookies));

        var csrf = cookies.Single(c => c.StartsWith("sqm_csrf=", StringComparison.Ordinal))
            .Split('=', 2)[1];

        return (client, csrf, await login.Content.ReadFromJsonAsync<JsonElement>(Ct));
    }

    private static async Task<HttpResponseMessage> SaveAsync(
        HttpClient client, string csrf, object settings)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/v1/auth/me/appearance")
        {
            Content = JsonContent.Create(settings),
        };

        request.Headers.Add("X-CSRF-Token", csrf);

        return await client.SendAsync(request, Ct);
    }

    // ======================================================================= the contract

    [Fact]
    public void A_value_the_contract_offers_is_accepted() =>
        Assert.Null(AppearanceContract.Problem(new Dictionary<string, string>
        {
            ["theme"] = "dark",
            ["accent"] = "teal",
        }));

    [Theory]
    [InlineData("accent", "chartreuse")]
    [InlineData("theme", "Dark")]
    [InlineData("fontSize", "")]
    [InlineData("sparkle", "on")]
    public void Anything_else_is_refused(string setting, string value) =>
        Assert.NotNull(AppearanceContract.Problem(new Dictionary<string, string> { [setting] = value }));

    [Fact]
    public void A_setting_left_out_takes_its_default()
    {
        var complete = AppearanceContract.Complete(
            new Dictionary<string, string> { ["accent"] = "teal" });

        Assert.Equal("teal", complete["accent"]);
        Assert.Equal("system", complete["theme"]);
        Assert.Equal(9, complete.Count);
    }

    [Fact]
    public void A_refusal_does_not_repeat_a_long_value_back()
    {
        var problem = AppearanceContract.Problem(new Dictionary<string, string>
        {
            ["accent"] = new string('x', 5000),
        });

        Assert.True(problem!.Length < 100);
    }

    // ======================================================================= over the API

    [Fact]
    public async Task Someone_who_has_saved_nothing_is_told_so()
    {
        SkipWithoutDatabase();
        var (_, username) = await CreateAsync();

        var (client, _, user) = await SignInAsync(username);
        using (client)
        {
            Assert.Equal(JsonValueKind.Null, user.GetProperty("appearance").ValueKind);
        }
    }

    [Fact]
    public async Task What_is_saved_comes_back_with_the_user()
    {
        SkipWithoutDatabase();
        var (_, username) = await CreateAsync();

        var (client, csrf, _) = await SignInAsync(username);
        using (client)
        {
            using var save = await SaveAsync(client, csrf, new { accent = "violet", theme = "dark" });
            Assert.Equal(HttpStatusCode.NoContent, save.StatusCode);

            var me = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/me", Ct);
            var appearance = me.GetProperty("appearance");

            Assert.Equal("violet", appearance.GetProperty("accent").GetString());
            Assert.Equal("dark", appearance.GetProperty("theme").GetString());
            Assert.Equal("normal", appearance.GetProperty("density").GetString());
        }
    }

    [Fact]
    public async Task It_follows_a_person_to_a_new_sign_in()
    {
        SkipWithoutDatabase();
        var (_, username) = await CreateAsync();

        var (first, csrf, _) = await SignInAsync(username);
        using (first)
        {
            using var save = await SaveAsync(first, csrf, new { fontSize = "larger" });
        }

        var (second, _, user) = await SignInAsync(username);
        using (second)
        {
            Assert.Equal("larger", user.GetProperty("appearance").GetProperty("fontSize").GetString());
        }
    }

    [Fact]
    public async Task A_second_save_replaces_the_first()
    {
        SkipWithoutDatabase();
        var (id, username) = await CreateAsync();

        var (client, csrf, _) = await SignInAsync(username);
        using (client)
        {
            using var first = await SaveAsync(client, csrf, new { accent = "violet" });
            using var second = await SaveAsync(client, csrf, new { accent = "teal" });
        }

        var saved = await _fixture.ScalarAsync<string>(
            "SELECT settings->>'accent' FROM auth.user_appearance WHERE user_id = @id", ("id", id));

        Assert.Equal("teal", saved);
    }

    [Fact]
    public async Task One_persons_settings_are_not_anothers()
    {
        SkipWithoutDatabase();
        var (_, mine) = await CreateAsync();
        var (_, theirs) = await CreateAsync();

        var (client, csrf, _) = await SignInAsync(mine);
        using (client)
        {
            using var save = await SaveAsync(client, csrf, new { accent = "magenta" });
        }

        var (other, _, user) = await SignInAsync(theirs);
        using (other)
        {
            Assert.Equal(JsonValueKind.Null, user.GetProperty("appearance").ValueKind);
        }
    }

    [Fact]
    public async Task A_value_outside_the_contract_is_refused_and_nothing_is_stored()
    {
        SkipWithoutDatabase();
        var (id, username) = await CreateAsync();

        var (client, csrf, _) = await SignInAsync(username);
        using (client)
        {
            using var response = await SaveAsync(client, csrf, new { accent = "chartreuse" });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        var rows = await _fixture.ScalarAsync<long>(
            "SELECT count(*) FROM auth.user_appearance WHERE user_id = @id", ("id", id));

        Assert.Equal(0, rows);
    }

    [Fact]
    public async Task Nobody_can_save_without_signing_in()
    {
        using var client = _factory.CreateClient();

        using var response = await client.PutAsJsonAsync(
            "/api/v1/auth/me/appearance", new { accent = "teal" }, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Saving_needs_the_csrf_token_like_any_other_write()
    {
        SkipWithoutDatabase();
        var (_, username) = await CreateAsync();

        var (client, _, _) = await SignInAsync(username);
        using (client)
        {
            using var response = await client.PutAsJsonAsync(
                "/api/v1/auth/me/appearance", new { accent = "teal" }, Ct);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }
}
