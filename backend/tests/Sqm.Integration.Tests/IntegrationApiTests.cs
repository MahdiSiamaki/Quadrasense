using System.Collections.Frozen;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Sqm.Api.Integration;
using Sqm.Application.Abstractions;
using Sqm.Application.Identity;
using Sqm.Application.Integration;
using Sqm.Application.Risk;
using Sqm.Application.Timeline;
using Sqm.Contracts.Integration;
using Sqm.Contracts.Lookup;
using Sqm.Domain.Bindings;
using Sqm.Domain.Identifiers;
using Sqm.Domain.Timeline;

namespace Sqm.Integration.Tests;

/// <summary>
/// The integration API end to end through the real host (ADR-024): the token endpoint, bearer
/// authentication, scopes, masking, the inventory reads, declared events, the feeds, rate limits and the
/// administrators' routes. The stores are in memory; the rules they apply are the real ones.
/// </summary>
public sealed class IntegrationApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Secret = "s3cret-for-the-test-client-only-0000000000";
    private const ulong Number = 9121234567, Sim = 432110123456789, OtherSim = 432110987654321;
    private const string Imei = "35085748000001", OtherImei = "35326911000002";

    private static readonly Dictionary<string, AuthenticatedUser> People = new(StringComparer.Ordinal)
    {
        ["admin"] = new(1, "admin", "Admin", false, new[] { Permissions.IntegrationManage }.ToFrozenSet(StringComparer.Ordinal)),
        ["analyst"] = new(2, "analyst", "Analyst", false,
            new[] { Permissions.LookupSubscriber, Permissions.IdentifierReveal }.ToFrozenSet(StringComparer.Ordinal)),
    };

    private readonly WebApplicationFactory<Program> _factory;
    private readonly List<AuditEntry> _audited = [];
    private readonly InMemoryIntegrationClients _clients = new();
    private readonly InMemoryDeclaredEvents _declared = new();
    private readonly InMemoryOutbox _outbox = new();
    private readonly List<(TimelineCentre Centre, string Digits)> _timelineReads = [];
    private readonly List<(DateOnly Day, BindingChangeCursor? After, int Limit)> _changeReads = [];
    private HistoryReadiness _readiness = HistoryReadiness.Ready;
    private int _perMinute = 1000;

    public IntegrationApiTests(WebApplicationFactory<Program> factory) =>
        _factory = factory.WithWebHostBuilder(host => host.ConfigureTestServices(services =>
        {
            services.AddSingleton<IIntegrationClientStore>(_clients);
            services.AddSingleton<IDeclaredEventStore>(_declared);
            services.AddSingleton<IIntegrationOutbox>(_outbox);
            services.AddSingleton(_ => new IntegrationRateLimiter(Options.Create(new IntegrationOptions { RequestsPerMinute = _perMinute })));
            services.AddSingleton(TestStubs.Create<IAuditLog>((_, args) =>
            {
                lock (_audited)
                {
                    _audited.Add((AuditEntry)args[0]!);
                }

                return Task.CompletedTask;
            }));
            services.AddSingleton(TestStubs.Create<ISessionStore>((method, args) => method.Name switch
            {
                "ResolveAsync" => Task.FromResult<ResolvedSession?>(People.TryGetValue((string)args[0]!, out var person)
                    ? new ResolvedSession(Guid.NewGuid(), person, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1), DateTimeOffset.UtcNow.AddHours(1))
                    : null),
                "TouchAsync" => Task.CompletedTask,
                "DeleteExpiredAsync" => Task.FromResult(0),
                _ => throw new NotSupportedException(method.Name),
            }));
            services.AddSingleton(TestStubs.Create<ITimelineStore>((method, args) => method.Name switch
            {
                "GetReadinessAsync" => Task.FromResult(_readiness),
                "DataThroughAsync" => Task.FromResult<DateOnly?>(new DateOnly(2026, 10, 31)),
                "GetAsync" => Timeline((TimelineCentre)args[0]!, (string)args[1]!),
                _ => throw new NotSupportedException(method.Name),
            }));
            services.AddSingleton(TestStubs.Create<IDeviceAnalyticsStore>((method, args) => method.Name switch
            {
                "GetDeviceAsync" => Task.FromResult<DeviceDetailRow?>((string)args[0]! == "35085748" ? Device() : null),
                "GetRelationshipsAsync" => Task.FromResult(new RelationshipGraph(
                    (string)args[1]!, (RelatedKind)args[0]!, true,
                    [new RelatedNode(Number.ToString(), RelatedKind.Msisdn, 1, 1, null)],
                    [new RelatedNode(Sim.ToString(), RelatedKind.Imsi, 1, 1, null)],
                    [new RelatedNode(Imei, RelatedKind.Imei, 1, 1, null, "35085748", "Samsung", "Galaxy A32", "Smartphone")],
                    [], [], false, 3, 10)),
                _ => throw new NotSupportedException(method.Name),
            }));
            services.AddSingleton(TestStubs.Create<IBindingChangeReader>((_, args) =>
            {
                var day = (DateOnly)args[0]!;
                var after = (BindingChangeCursor?)args[1];
                var limit = (int)args[2]!;
                lock (_changeReads)
                {
                    _changeReads.Add((day, after, limit));
                }

                return Task.FromResult(after is null
                    ? new BindingChangePage(
                        [new DailyBindingChange(day, 250, Number, Sim, Imei, "35085748", true)],
                        new BindingChangeCursor(Number, Sim, Imei, 250))
                    : new BindingChangePage([new DailyBindingChange(day, 250, Number, OtherSim, OtherImei, "35326911", false)], null));
            }));
            services.AddSingleton(TestStubs.Create<IRiskReader>((method, _) => method.Name switch
            {
                "GetStateAsync" => Task.FromResult(new RiskReadState(true, null, null, null, new HashSet<DateOnly>())),
                _ => throw new NotSupportedException(method.Name),
            }));
        }));

    // ------------------------------------------------------------------ the token endpoint

    [Fact]
    public async Task A_client_trades_its_secret_for_a_short_token_carrying_its_scopes()
    {
        _clients.Add("lodestone", Secret, true, IntegrationScopes.InventoryRead, IntegrationScopes.EventsRead);

        using var response = await TokenAsync(basic: ("lodestone", Secret));
        var body = await JsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Bearer", body.GetProperty("token_type").GetString());
        Assert.Equal(900, body.GetProperty("expires_in").GetInt32());
        Assert.Equal("events.read inventory.read", body.GetProperty("scope").GetString());
        Assert.True(IntegrationCredentials.IsWellFormed(body.GetProperty("access_token").GetString()));
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.Ordinal);

        var issued = Assert.Single(_audited, a => a.Action == "integration.token.issued");
        Assert.Equal("client:lodestone", issued.ActorName);
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(_audited), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Form_credentials_work_and_a_narrower_scope_can_be_asked_for()
    {
        _clients.Add("tessera", Secret, true, IntegrationScopes.FactsRead, IntegrationScopes.InventoryRead);

        using var response = await TokenAsync(form: ("tessera", Secret), scope: "facts.read");
        var body = await JsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("facts.read", body.GetProperty("scope").GetString());
    }

    [Theory]
    [InlineData("lodestone", "wrong-secret")]
    [InlineData("nobody", Secret)]
    [InlineData("retired", Secret)]
    public async Task A_wrong_secret_an_unknown_client_and_an_inactive_one_are_all_invalid_client(string client, string secret)
    {
        _clients.Add("lodestone", Secret, true, IntegrationScopes.InventoryRead);
        _clients.Add("retired", Secret, false, IntegrationScopes.InventoryRead);

        using var response = await TokenAsync(basic: (client, secret));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("invalid_client", (await JsonAsync(response)).GetProperty("error").GetString());
        Assert.Equal("Basic", response.Headers.WwwAuthenticate.Single().Scheme);
        Assert.Contains(_audited, a => a.Action == "integration.token.refused" && a.Outcome == AuditOutcome.Failure);
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(_audited), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_scope_the_client_does_not_hold_or_nobody_knows_is_invalid_scope()
    {
        _clients.Add("lodestone", Secret, true, IntegrationScopes.InventoryRead);

        using var notHeld = await TokenAsync(basic: ("lodestone", Secret), scope: "events.write");
        using var unknown = await TokenAsync(basic: ("lodestone", Secret), scope: "admin.everything");

        Assert.Equal("invalid_scope", (await JsonAsync(notHeld)).GetProperty("error").GetString());
        Assert.Equal("invalid_scope", (await JsonAsync(unknown)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Only_the_client_credentials_grant_sent_as_a_form_is_accepted()
    {
        _clients.Add("lodestone", Secret, true, IntegrationScopes.InventoryRead);

        using var client = _factory.CreateClient();
        using var password = await client.PostAsync("/api/integration/v1/oauth/token",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "password" }), Ct);
        using var json = await client.PostAsJsonAsync("/api/integration/v1/oauth/token", new { grant_type = "client_credentials" }, Ct);

        Assert.Equal("unsupported_grant_type", (await JsonAsync(password)).GetProperty("error").GetString());
        Assert.Equal("invalid_request", (await JsonAsync(json)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task The_token_endpoint_is_limited_per_address()
    {
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 31; i++)
        {
            using var response = await TokenAsync(basic: ("nobody", "x"));
            statuses.Add(response.StatusCode);
        }

        Assert.Equal(30, statuses.Count(s => s == HttpStatusCode.Unauthorized));
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);
    }

    // ------------------------------------------------------------------ bearer authentication and scopes

    [Fact]
    public async Task No_token_is_401_with_a_bearer_challenge_and_a_bad_one_says_invalid_token()
    {
        using var none = await SendAsync(HttpMethod.Get, "/api/integration/v1/status", null, bearer: null);
        using var bad = await SendAsync(HttpMethod.Get, "/api/integration/v1/status", null, bearer: IntegrationCredentials.NewValue());

        Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);
        Assert.Equal("Bearer", none.Headers.WwwAuthenticate.Single().Scheme);
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
        Assert.Contains("invalid_token", bad.Headers.WwwAuthenticate.Single().Parameter, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_expired_token_is_refused()
    {
        _clients.Lifetime = TimeSpan.FromSeconds(-1);
        var token = _clients.Issue(_clients.Add("lodestone", Secret, true, IntegrationScopes.InventoryRead), IntegrationScopes.InventoryRead);

        using var response = await SendAsync(HttpMethod.Get, "/api/integration/v1/status", null, token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_token_without_the_scope_is_403_and_the_denial_is_audited()
    {
        var token = Token(IntegrationScopes.EventsRead);

        using var response = await SendAsync(HttpMethod.Post, "/api/integration/v1/inventory/lookup", new { identifier = Number.ToString() }, token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("insufficient_scope", response.Headers.WwwAuthenticate.Single().Parameter, StringComparison.Ordinal);
        var denied = Assert.Single(_audited, a => a.Outcome == AuditOutcome.Denied);
        Assert.Equal(("client:lodestone", "inventory.read"), (denied.ActorName, denied.Action));
    }

    [Fact]
    public async Task A_session_opens_no_integration_route_and_a_token_opens_no_UI_route()
    {
        var token = Token(IntegrationScopes.InventoryRead, IntegrationScopes.IdentifiersReveal);

        using var withSession = await SendAsync(HttpMethod.Post, "/api/integration/v1/inventory/lookup",
            new { identifier = Number.ToString() }, bearer: null, session: "analyst");
        using var withToken = await SendAsync(HttpMethod.Post, "/api/v1/lookup/msisdn", new { msisdn = Number.ToString() }, token);

        Assert.Equal(HttpStatusCode.Unauthorized, withSession.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, withToken.StatusCode);
    }

    [Fact]
    public async Task A_client_over_its_rate_is_429_with_retry_after()
    {
        _perMinute = 2;
        var token = Token(IntegrationScopes.InventoryRead);

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
        {
            using var response = await SendAsync(HttpMethod.Get, "/api/integration/v1/status", null, token);
            statuses.Add(response.StatusCode);
            if (i == 2)
            {
                Assert.NotNull(response.Headers.RetryAfter);
            }
        }

        Assert.Equal(new[] { HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests }, statuses.ToArray());
    }

    // ------------------------------------------------------------------ inventory

    [Fact]
    public async Task A_lookup_without_reveal_shows_only_what_the_client_sent()
    {
        var token = Token(IntegrationScopes.InventoryRead);

        var (status, body) = await PostAsync("/api/integration/v1/inventory/lookup", new { identifier = Number.ToString() }, token);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(("msisdn", "masked", true), (body.GetProperty("kind").GetString(), body.GetProperty("identifiers").GetString(), body.GetProperty("found").GetBoolean()));
        var binding = Binding(body, "35085748");
        Assert.Equal(Number.ToString(), binding.GetProperty("msisdn").GetString());
        Assert.Equal(IdentifierMask.Imsi(Sim.ToString()), binding.GetProperty("imsi").GetString());
        Assert.Equal(IdentifierMask.Imei(Imei), binding.GetProperty("imei").GetString());
        Assert.Equal(IdentifierMask.Iccid(Iccid.FromImsi(Sim)!), binding.GetProperty("iccid").GetString());
    }

    [Fact]
    public async Task A_lookup_with_reveal_is_complete_and_its_ICCID_is_derived_and_says_so()
    {
        var token = Token(IntegrationScopes.InventoryRead, IntegrationScopes.IdentifiersReveal);

        var (_, body) = await PostAsync("/api/integration/v1/inventory/lookup", new { identifier = Number.ToString(), kind = "msisdn" }, token);

        var binding = Binding(body, "35085748");
        Assert.Equal((Sim.ToString(), Imei, Iccid.FromImsi(Sim), "derived"), (
            binding.GetProperty("imsi").GetString(), binding.GetProperty("imei").GetString(),
            binding.GetProperty("iccid").GetString(), binding.GetProperty("iccidSource").GetString()));
        Assert.Equal(2, body.GetProperty("summary").GetProperty("bindings").GetInt32());
        Assert.Equal("2026-10-31", body.GetProperty("dataThrough").GetString());
    }

    [Fact]
    public async Task An_ICCID_is_resolved_to_its_IMSI_by_the_operators_numbering()
    {
        var token = Token(IntegrationScopes.InventoryRead);
        var iccid = Iccid.FromImsi(Sim)!;

        var (status, body) = await PostAsync("/api/integration/v1/inventory/lookup", new { identifier = iccid }, token);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(("iccid", Sim.ToString()), (body.GetProperty("kind").GetString(), body.GetProperty("resolvedImsi").GetString()));
        Assert.Equal((TimelineCentre.Imsi, Sim.ToString()), _timelineReads.Single());
        // The SIM the client named by ICCID is the one it already knows: neither form of it is masked.
        var binding = Binding(body, "35085748");
        Assert.Equal((Sim.ToString(), iccid), (binding.GetProperty("imsi").GetString(), binding.GetProperty("iccid").GetString()));
    }

    [Theory]
    [InlineData("89043051200000000000000000000123", "EID")]
    [InlineData("8901260000000000001", "ICCID")]
    [InlineData("12345", "phone number")]
    public async Task An_identifier_PI_cannot_answer_is_refused_with_the_reason(string identifier, string reason)
    {
        var token = Token(IntegrationScopes.InventoryRead);

        var (status, body) = await PostAsync("/api/integration/v1/inventory/lookup", new { identifier }, token);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains(reason, body.GetProperty("errors").GetProperty("identifier")[0].GetString(), StringComparison.Ordinal);
        Assert.Empty(_timelineReads);
    }

    [Fact]
    public async Task No_identifier_ever_reaches_the_audit_log()
    {
        var token = Token(IntegrationScopes.InventoryRead, IntegrationScopes.IdentifiersReveal, IntegrationScopes.FactsRead);

        await PostAsync("/api/integration/v1/inventory/lookup", new { identifier = Number.ToString() }, token);
        await PostAsync("/api/integration/v1/inventory/history", new { identifier = Sim.ToString() }, token);
        await PostAsync("/api/integration/v1/inventory/facts", new { identifier = Imei }, token);
        await PostAsync("/api/integration/v1/inventory/relationships", new { identifier = Imei }, token);

        var log = JsonSerializer.Serialize(_audited);
        Assert.Equal(4, _audited.Count(a => a.Outcome == AuditOutcome.Success && a.Action.StartsWith("integration.inventory.", StringComparison.Ordinal)));
        foreach (var value in new[] { Number.ToString(), Sim.ToString(), Imei, OtherImei, Iccid.FromImsi(Sim)! })
        {
            Assert.DoesNotContain(value, log, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_batch_answers_each_identifier_on_its_own_and_refuses_too_many()
    {
        var token = Token(IntegrationScopes.InventoryRead);

        var (status, body) = await PostAsync("/api/integration/v1/inventory/lookup/batch",
            new { identifiers = new object[] { new { identifier = Number.ToString() }, new { identifier = "x" }, new { identifier = Imei, kind = "imei" } } }, token);
        var (tooMany, _) = await PostAsync("/api/integration/v1/inventory/lookup/batch",
            new { identifiers = Enumerable.Range(0, 51).Select(_ => new { identifier = Number.ToString() }).ToArray() }, token);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(new[] { "found", "invalid", "found" }, body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("outcome").GetString()!).ToArray());
        Assert.Equal(HttpStatusCode.BadRequest, tooMany);
    }

    [Fact]
    public async Task The_history_says_so_while_the_binding_history_is_being_built()
    {
        _readiness = HistoryReadiness.Building;
        var token = Token(IntegrationScopes.InventoryRead);

        var (status, _) = await PostAsync("/api/integration/v1/inventory/history", new { identifier = Number.ToString() }, token);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
    }

    [Fact]
    public async Task Facts_carry_the_handsets_model_and_say_why_the_risk_measures_are_missing()
    {
        var token = Token(IntegrationScopes.FactsRead);

        var (status, body) = await PostAsync("/api/integration/v1/inventory/facts", new { identifier = Imei }, token);

        Assert.Equal(HttpStatusCode.OK, status);
        var model = body.GetProperty("devices")[0].GetProperty("model");
        Assert.Equal(("Samsung", true, true), (
            model.GetProperty("brand").GetString(), model.GetProperty("capabilities").GetProperty("lte").GetBoolean(),
            model.GetProperty("capabilities").GetProperty("esim").GetBoolean()));
        Assert.Equal(JsonValueKind.Null, body.GetProperty("risk").ValueKind);
        Assert.False(string.IsNullOrEmpty(body.GetProperty("riskUnavailable").GetString()));
        Assert.Equal(Imei, body.GetProperty("devices")[0].GetProperty("imei").GetString());
    }

    [Fact]
    public async Task A_device_model_is_read_by_its_TAC_and_an_unknown_one_is_404()
    {
        var token = Token(IntegrationScopes.InventoryRead);

        using var known = await SendAsync(HttpMethod.Get, "/api/integration/v1/devices/models/35085748", null, token);
        using var unknown = await SendAsync(HttpMethod.Get, "/api/integration/v1/devices/models/99999999", null, token);

        Assert.Equal(HttpStatusCode.OK, known.StatusCode);
        Assert.Equal("Galaxy A32", (await JsonAsync(known)).GetProperty("marketingName").GetString());
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    // ------------------------------------------------------------------ sync

    [Fact]
    public async Task A_days_changes_page_on_with_an_opaque_cursor_that_never_travels_in_a_URL()
    {
        var token = Token(IntegrationScopes.InventorySync, IntegrationScopes.IdentifiersReveal);

        var (first, page) = await PostAsync("/api/integration/v1/inventory/changes", new { date = "2026-10-31", limit = 1 }, token);
        var cursor = page.GetProperty("next").GetString()!;
        var (_, second) = await PostAsync("/api/integration/v1/inventory/changes", new { date = "2026-10-31", cursor, limit = 1 }, token);
        var (badCursor, _) = await PostAsync("/api/integration/v1/inventory/changes", new { date = "2026-10-31", cursor = "nonsense!" }, token);
        var (badDate, _) = await PostAsync("/api/integration/v1/inventory/changes", new { date = "31/10/2026" }, token);

        Assert.Equal(HttpStatusCode.OK, first);
        Assert.Equal(("add", Imei), (page.GetProperty("changes")[0].GetProperty("change").GetString(), page.GetProperty("changes")[0].GetProperty("imei").GetString()));
        Assert.DoesNotContain(Number.ToString(), cursor, StringComparison.Ordinal);
        Assert.Equal(new BindingChangeCursor(Number, Sim, Imei, 250), _changeReads[1].After);
        Assert.Equal(JsonValueKind.Null, second.GetProperty("next").ValueKind);
        Assert.Equal(("remove", HttpStatusCode.BadRequest, HttpStatusCode.BadRequest),
            (second.GetProperty("changes")[0].GetProperty("change").GetString(), badCursor, badDate));
    }

    [Fact]
    public async Task A_cursor_reads_back_what_was_written_and_nothing_else()
    {
        var cursor = new BindingChangeCursor(Number, Sim, Imei, 250);

        Assert.Equal(cursor, IntegrationInventoryEndpoints.ReadCursor(IntegrationInventoryEndpoints.WriteCursor(cursor)));
        Assert.Null(IntegrationInventoryEndpoints.ReadCursor("bm90IGEgY3Vyc29y"));
        Assert.Null(IntegrationInventoryEndpoints.ReadCursor(Convert.ToBase64String(Encoding.UTF8.GetBytes("1:2:x' OR 1=1:3"))));
    }

    // ------------------------------------------------------------------ events

    [Fact]
    public async Task Declared_events_are_stored_once_per_id_and_a_reused_id_is_a_conflict()
    {
        var token = Token(IntegrationScopes.EventsWrite);
        var swap = SwapEvent("crm-1001");

        var (first, accepted) = await PostAsync("/api/integration/v1/events/declared", new { events = new[] { swap } }, token);
        var (retry, duplicate) = await PostAsync("/api/integration/v1/events/declared", new { events = new[] { swap } }, token);
        var (reuse, conflict) = await PostAsync("/api/integration/v1/events/declared",
            new { events = new[] { swap with { Msisdn = "9129999999" } } }, token);

        Assert.Equal((HttpStatusCode.OK, 1), (first, accepted.GetProperty("accepted").GetInt32()));
        Assert.Equal((HttpStatusCode.OK, 1), (retry, duplicate.GetProperty("duplicates").GetInt32()));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "conflict"), (reuse, conflict.GetProperty("results")[0].GetProperty("outcome").GetString()));
        Assert.Equal(1, _declared.Count);
    }

    [Fact]
    public async Task An_invalid_declared_event_is_refused_by_field_and_its_valid_neighbours_are_kept()
    {
        var token = Token(IntegrationScopes.EventsWrite);
        var bad = new { eventId = "crm-2", source = "crm", type = "sim-swap", occurredAt = DateTimeOffset.UtcNow, msisdn = "123", imsi = Sim.ToString() };

        var (status, body) = await PostAsync("/api/integration/v1/events/declared", new { events = new object[] { SwapEvent("crm-1"), bad } }, token);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        var problems = body.GetProperty("results")[1].GetProperty("problems");
        Assert.True(problems.TryGetProperty("msisdn", out _));
        Assert.True(problems.TryGetProperty("previousImsi", out _));
        Assert.Equal(1, _declared.Count);
    }

    [Fact]
    public async Task What_was_declared_appears_in_the_history_of_the_number_and_of_either_SIM()
    {
        var write = Token(IntegrationScopes.EventsWrite);
        await PostAsync("/api/integration/v1/events/declared", new { events = new[] { SwapEvent("crm-7") } }, write);
        var read = Token(IntegrationScopes.InventoryRead, IntegrationScopes.IdentifiersReveal);

        var (_, byNumber) = await PostAsync("/api/integration/v1/inventory/history", new { identifier = Number.ToString() }, read);
        var (_, byOldSim) = await PostAsync("/api/integration/v1/inventory/history", new { identifier = OtherSim.ToString() }, read);
        var (_, byHandset) = await PostAsync("/api/integration/v1/inventory/history", new { identifier = Imei }, read);

        Assert.Equal("sim-swap", byNumber.GetProperty("declared")[0].GetProperty("type").GetString());
        Assert.Equal("crm-7", byOldSim.GetProperty("declared")[0].GetProperty("eventId").GetString());
        Assert.Equal(0, byHandset.GetProperty("declared").GetArrayLength());
    }

    [Fact]
    public async Task The_event_feed_resumes_after_a_cursor()
    {
        await _outbox.AppendAsync(IntegrationEventTypes.DayImported, "2026-10-30", new { businessDate = "2026-10-30" }, Ct);
        await _outbox.AppendAsync(IntegrationEventTypes.DayImported, "2026-10-31", new { businessDate = "2026-10-31" }, Ct);
        await _outbox.AppendAsync(IntegrationEventTypes.CatalogueActivated, "v2026.10.04", new { versionLabel = "v2026.10.04" }, Ct);
        var token = Token(IntegrationScopes.EventsRead);

        using var response = await SendAsync(HttpMethod.Get, "/api/integration/v1/events?after=1&limit=1", null, token);
        var body = await JsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var only = body.GetProperty("events")[0];
        Assert.Equal((2L, "2026-10-31", "inventory.day-imported"), (only.GetProperty("sequence").GetInt64(), only.GetProperty("subject").GetString(), only.GetProperty("type").GetString()));
        Assert.Equal((2L, 1L, 3L), (body.GetProperty("next").GetInt64(), body.GetProperty("oldestAvailable").GetInt64(), body.GetProperty("latest").GetInt64()));
    }

    // ------------------------------------------------------------------ administration

    [Fact]
    public async Task An_administrator_registers_a_client_whose_secret_is_shown_once_and_works()
    {
        using var created = await SendAsync(HttpMethod.Post, "/api/v1/integration/clients",
            new { clientId = "lodestone", displayName = "Lodestone (IEH)", description = "The hub", scopes = new[] { "inventory.read", "events.read" } },
            bearer: null, session: "admin");
        var body = await JsonAsync(created);
        var secret = body.GetProperty("clientSecret").GetString()!;

        using var listed = await SendAsync(HttpMethod.Get, "/api/v1/integration/clients", null, bearer: null, session: "admin");
        using var token = await TokenAsync(basic: ("lodestone", secret));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.DoesNotContain(secret, await listed.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, token.StatusCode);
        Assert.Contains(_audited, a => a.Action == "integration.client.create" && a.TargetName == "lodestone");
    }

    [Fact]
    public async Task Registering_a_client_needs_integration_manage_and_valid_scopes()
    {
        using var refused = await SendAsync(HttpMethod.Post, "/api/v1/integration/clients",
            new { clientId = "lodestone", displayName = "x", scopes = new[] { "inventory.read" } }, bearer: null, session: "analyst");
        using var invalid = await SendAsync(HttpMethod.Post, "/api/v1/integration/clients",
            new { clientId = "Lodestone!", displayName = "x", scopes = new[] { "root" } }, bearer: null, session: "admin");

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        var errors = (await JsonAsync(invalid)).GetProperty("errors");
        Assert.True(errors.TryGetProperty("clientId", out _));
        Assert.True(errors.TryGetProperty("scopes", out _));
    }

    [Fact]
    public async Task Deactivating_a_client_ends_its_tokens_at_once()
    {
        var id = _clients.Add("lodestone", Secret, true, IntegrationScopes.InventoryRead);
        var token = _clients.Issue(id, IntegrationScopes.InventoryRead);

        using var deactivated = await SendAsync(HttpMethod.Put, $"/api/v1/integration/clients/{id}",
            new { displayName = "Lodestone", scopes = new[] { "inventory.read" }, isActive = false }, bearer: null, session: "admin");
        using var after = await SendAsync(HttpMethod.Get, "/api/integration/v1/status", null, token);

        Assert.Equal(HttpStatusCode.OK, deactivated.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    // ------------------------------------------------------------------ guards

    [Fact]
    public void No_integration_contract_can_carry_a_key_value()
    {
        // PI holds key references at most, never values (CLAUDE.md, 2026-11-02): no answer may even have the field.
        string[] forbidden = ["ki", "opc", "op", "k4", "kic", "kid", "kik", "key", "keyvalue", "transportkey", "otakey"];
        var assembly = typeof(Contracts.Integration.InventoryLookupResponse).Assembly;
        var offending = assembly.GetTypes()
            .Where(t => t.Namespace == "Sqm.Contracts.Integration")
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => $"{t.Name}.{p.Name}"))
            .Where(name => forbidden.Contains(name[(name.IndexOf('.', StringComparison.Ordinal) + 1)..], StringComparer.OrdinalIgnoreCase))
            .ToList();

        Assert.Empty(offending);
    }

    [Fact]
    public async Task The_integration_document_lists_its_routes_and_the_oauth_flow_and_no_UI_route()
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/openapi/integration-v1.json", Ct);
        var document = await JsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var paths = document.GetProperty("paths").EnumerateObject().Select(p => p.Name).ToList();
        Assert.Contains("/api/integration/v1/inventory/lookup", paths);
        Assert.Contains("/api/integration/v1/oauth/token", paths);
        Assert.All(paths, p => Assert.StartsWith("/api/integration/v1/", p, StringComparison.Ordinal));
        var flow = document.GetProperty("components").GetProperty("securitySchemes").GetProperty("oauth2").GetProperty("flows").GetProperty("clientCredentials");
        Assert.Equal("/api/integration/v1/oauth/token", flow.GetProperty("tokenUrl").GetString());
    }

    // ------------------------------------------------------------------ helpers

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JsonElement Binding(JsonElement lookup, string tac) =>
        lookup.GetProperty("bindings").EnumerateArray().Single(b => b.GetProperty("tac").GetString() == tac);

    private string Token(params string[] scopes) =>
        _clients.Issue(_clients.Add("lodestone", Secret, true, scopes), scopes);

    private static DeclaredEventInput SwapEvent(string id) => new(
        id, "crm", "sim-swap", new DateTimeOffset(2026, 10, 30, 9, 15, 0, TimeSpan.FromHours(3.5)), Number.ToString(),
        Imsi: Sim.ToString(), PreviousImsi: OtherSim.ToString(), Reference: "ORD-42");

    private Task<StoredTimeline> Timeline(TimelineCentre centre, string digits)
    {
        lock (_timelineReads)
        {
            _timelineReads.Add((centre, digits));
        }

        return Task.FromResult(new StoredTimeline(
        [
            new StoredBinding(Number, Sim, Imei, "35085748", "Samsung", "Galaxy A32", false,
                [new BindingEvent(new DateOnly(2026, 9, 1), 220, ChangeLabel.Add)], true),
            new StoredBinding(Number, OtherSim, OtherImei, "35326911", "Xiaomi", "Redmi Note 13", true,
                [new BindingEvent(new DateOnly(2026, 8, 31), 219, ChangeLabel.Remove)], false),
        ], 2, 3, 4_096));
    }

    private static DeviceDetailRow Device() => new(
        new DeviceRow("35085748", "Samsung Korea", "Samsung", "Samsung", "SM-A325F", "Galaxy A32", "Smartphone", "Android",
            10, 10, 10, 10, null, null),
        null, null, "2021-02-01", null, "Y", "Y", "Y", "2", "2", "LTE FDD BAND 3", true, false, true, false, 10, 10, 10, true, 7);

    private async Task<HttpResponseMessage> TokenAsync(
        (string Id, string Secret)? basic = null, (string Id, string Secret)? form = null, string? scope = null)
    {
        var fields = new Dictionary<string, string> { ["grant_type"] = "client_credentials" };
        if (form is { } f)
        {
            fields["client_id"] = f.Id;
            fields["client_secret"] = f.Secret;
        }

        if (scope is not null)
        {
            fields["scope"] = scope;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/integration/v1/oauth/token") { Content = new FormUrlEncodedContent(fields) };
        if (basic is { } b)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Uri.EscapeDataString(b.Id)}:{Uri.EscapeDataString(b.Secret)}")));
        }

        using var client = _factory.CreateClient();
        return await client.SendAsync(request, Ct);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? json, string? bearer, string? session = null)
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(method, path);
        if (json is not null)
        {
            request.Content = JsonContent.Create(json);
        }

        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        if (session is not null)
        {
            request.Headers.Add("Cookie", $"sqm_session={session}; sqm_csrf=t");
            request.Headers.Add("X-CSRF-Token", "t");
        }

        return await client.SendAsync(request, Ct);
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(string path, object body, string token)
    {
        using var response = await SendAsync(HttpMethod.Post, path, body, token);
        return (response.StatusCode, await JsonAsync(response));
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        return string.IsNullOrEmpty(text) ? default : JsonDocument.Parse(text).RootElement.Clone();
    }
}
