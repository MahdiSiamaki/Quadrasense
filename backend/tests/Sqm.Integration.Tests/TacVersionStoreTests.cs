using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sqm.Infrastructure.ClickHouse;
using Sqm.Infrastructure.DataImport;

namespace Sqm.Integration.Tests;

/// <summary>
/// What an administrator reads before activating a GSMA version: the newest lastUpdatedDate in the
/// file, and how many active bindings the new version would change.
/// </summary>
/// <remarks>
/// <para>
/// Both were wrong on the 4 October 2026 file. The newest date was a string max - "31-Oct-2025" beats
/// "02-Oct-2026" alphabetically - and the affected count kept the TACs with one fingerprint across the
/// two versions, which are the unchanged ones: 113,038,260 bindings reported for 153 changed TACs.
/// </para>
/// <para>
/// The affected count is read from the per-model mart at the last published delivery. It used to scan
/// current state with FINAL, and on 2026-11-10 that scan outlasted its timeout three times on the
/// department's server and failed the import of the 9 November catalogue.
/// </para>
/// <para>Against the real ClickHouse, through the real store, in a scratch database.</para>
/// </remarks>
[Collection("clickhouse-scratch")]
public sealed class TacVersionStoreTests : IAsyncLifetime
{
    private const string Endpoint = "http://localhost:18123/";
    private const string User = "sqm_ingest";
    private const string Password = "sqm_dev";

    private readonly string _database = "itest_tacver_" + Guid.NewGuid().ToString("N")[..12];
    private readonly HttpClient _http = new(new SocketsHttpHandler { PooledConnectionIdleTimeout = TimeSpan.FromSeconds(5) })
    {
        Timeout = TimeSpan.FromMinutes(2),
    };
    private string? _unavailable;

    public async ValueTask InitializeAsync()
    {
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{User}:{Password}")));

        try
        {
            if (await QueryAsync("EXISTS TABLE sqm.tac_all") != "1")
            {
                _unavailable = "ClickHouse is up but has no sqm schema to copy";
                return;
            }

            await QueryAsync($"CREATE DATABASE {_database}");
            await QueryAsync($"CREATE TABLE {_database}.tac_all AS sqm.tac_all");
            await QueryAsync($"CREATE TABLE {_database}.agg_device_model AS sqm.agg_device_model");
            await QueryAsync($"CREATE TABLE {_database}.mart_ready AS sqm.mart_ready");

            // Version 1: A, B and C. Version 2: A as it was, B with new bands, C gone, D new.
            // D's date is the newest, and the one a string max gets wrong.
            await QueryAsync($"""
                INSERT INTO {_database}.tac_all (version_id, tac, manufacturer, lastUpdatedDate, bandDetails) VALUES
                    (1, '35000001', 'Samsung', '31-Oct-2025', 'GSM 900'),
                    (1, '35000002', 'Apple',   '02-Jan-2026', 'LTE'),
                    (1, '35000003', 'Nokia',   '15-Mar-2024', 'GSM 900'),
                    (2, '35000001', 'Samsung', '31-Oct-2025', 'GSM 900'),
                    (2, '35000002', 'Apple',   '14-Sep-2026', 'LTE, NR'),
                    (2, '35000004', 'Xiaomi',  '02-Oct-2026', 'LTE')
                """);

            // The per-model mart, which holds active bindings only. Delivery 7 is the last one published:
            // 5 on A (unchanged), 3 on B (changed), 2 on C (removed), 1 on D (added). Delivery 6 was
            // published before it, and delivery 8 is written but not published: neither may be read.
            await QueryAsync($"""
                INSERT INTO {_database}.agg_device_model (seq, tac, bindings, handsets, sims, subscribers) VALUES
                    (6, '35000002', 30, 30, 30, 30), (6, '35000003', 20, 20, 20, 20), (6, '35000004', 10, 10, 10, 10),
                    (7, '35000001', 5, 5, 5, 5), (7, '35000002', 3, 3, 3, 3), (7, '35000003', 2, 2, 2, 2), (7, '35000004', 1, 1, 1, 1),
                    (8, '35000002', 300, 300, 300, 300), (8, '35000004', 100, 100, 100, 100)
                """);
            await QueryAsync($"""
                INSERT INTO {_database}.mart_ready (seq, completed_at, statements) VALUES
                    (6, '2026-10-30 06:00:00', 31), (7, '2026-10-31 06:00:00', 31)
                """);
        }
        catch (HttpRequestException ex)
        {
            _unavailable = $"no ClickHouse at {Endpoint}: {ex.Message}";
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_unavailable is null)
        {
            await QueryAsync($"DROP DATABASE IF EXISTS {_database}");
        }

        _http.Dispose();
    }

    [Fact]
    public async Task The_newest_update_is_read_as_a_date_not_as_text()
    {
        Assert.SkipWhen(_unavailable is not null, _unavailable ?? "");

        var check = await Store().CheckVersionAsync(2, TestContext.Current.CancellationToken);

        Assert.Equal("02-Oct-2026", check.LatestUpdateDate);
    }

    [Fact]
    public async Task Affected_bindings_are_the_active_ones_on_tacs_added_removed_or_changed()
    {
        Assert.SkipWhen(_unavailable is not null, _unavailable ?? "");

        var diff = await Store().DiffAsync(2, 1, TestContext.Current.CancellationToken);

        Assert.Equal((1, 1, 1, 1), (diff.Added, diff.Removed, diff.Updated, diff.Unchanged));
        // B's three, C's two and D's one at the last published delivery; never A's five, never another
        // delivery's figures (60 a day earlier, 400 in the one not yet published).
        Assert.Equal(6, diff.AffectedActiveBindings);
    }

    [Fact]
    public async Task Before_any_delivery_is_published_nothing_is_affected_and_nothing_fails()
    {
        Assert.SkipWhen(_unavailable is not null, _unavailable ?? "");
        await QueryAsync($"TRUNCATE TABLE {_database}.mart_ready");

        var diff = await Store().DiffAsync(2, 1, TestContext.Current.CancellationToken);

        Assert.Equal((1, 1, 1, 1), (diff.Added, diff.Removed, diff.Updated, diff.Unchanged));
        Assert.Equal(0, diff.AffectedActiveBindings);
    }

    private ClickHouseTacVersionStore Store() => new(
        new Options<ClickHouseOptions>(new ClickHouseOptions
        {
            ConnectionString = $"Host=localhost;Port=18123;Database={_database};Username={User};Password={Password}",
        }),
        new Factory(),
        NullLogger<ClickHouseTacVersionStore>.Instance);

    private async Task<string> QueryAsync(string sql)
    {
        using var response = await _http.PostAsync(
            Endpoint, new StringContent(sql, Encoding.UTF8, "text/plain"), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        return response.IsSuccessStatusCode ? body.Trim() : throw new InvalidOperationException($"ClickHouse refused: {body}");
    }

    private sealed class Factory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        })
        {
            Timeout = TimeSpan.FromMinutes(2),
        };
    }

    private sealed class Options<T>(T value) : IOptions<T> where T : class
    {
        public T Value { get; } = value;
    }
}
