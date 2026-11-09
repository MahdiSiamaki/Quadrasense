using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sqm.Application.Integration;
using Sqm.Application.Timeline;
using Sqm.Infrastructure.ClickHouse;

namespace Sqm.Integration.Tests;

/// <summary>
/// The integration API's reads against the real dataset (ADR-024): a day's changes paged by cursor, and a
/// lookup through the binding history. Skips with the reason when ClickHouse or the data is absent.
/// </summary>
public sealed class IntegrationRealDataTests
{
    private static string Connection(string user) => $"Host=localhost;Port=18123;Database=sqm;Username={user};Password=sqm_dev";

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

    private static void Write(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);

    private static ClickHouseTimelineStore Timelines() => new(
        Options.Create(new ClickHouseOptions { ConnectionString = Connection("sqm_app") }), new Factory(),
        NullLogger<ClickHouseTimelineStore>.Instance, TimeProvider.System);

    [Fact]
    public async Task A_days_changes_page_on_without_a_gap_or_a_repeat_each_within_its_budget()
    {
        var ct = TestContext.Current.CancellationToken;
        DateOnly? through;
        try
        {
            through = await Timelines().DataThroughAsync(ct);
        }
        catch (HttpRequestException ex)
        {
            Assert.Skip($"no ClickHouse at localhost:18123: {ex.Message}");
            return;
        }

        if (through is not { } day)
        {
            Assert.Skip("ClickHouse holds no day of the binding feed");
            return;
        }

        var reader = new ClickHouseBindingChangeReader(
            Options.Create(new ClickHouseOptions { ConnectionString = Connection("sqm_app") }), new Factory());

        var watch = Stopwatch.StartNew();
        var first = await reader.ReadAsync(day, null, 1000, ct);
        var firstMs = watch.ElapsedMilliseconds;
        Assert.Equal(1000, first.Changes.Count);
        Assert.NotNull(first.Next);

        watch.Restart();
        var second = await reader.ReadAsync(day, first.Next, 1000, ct);
        var secondMs = watch.ElapsedMilliseconds;

        // The second page starts strictly after the first page's last row: keyset order, no overlap.
        var last = first.Changes[^1];
        var next = second.Changes[0];
        Assert.True(Compare(next, last) > 0, "the second page must start after the first");
        Assert.All(first.Changes.Concat(second.Changes), c => Assert.Equal(day, c.Date));
        Assert.Empty(first.Changes.Select(Key).Intersect(second.Changes.Select(Key)));

        Write($"{day:yyyy-MM-dd}: page 1 {firstMs} ms, page 2 {secondMs} ms, adds {first.Changes.Count(c => c.Added)}/1000 on page 1");
        Assert.True(firstMs < 30_000 && secondMs < 30_000);
    }

    [Fact]
    public async Task A_number_from_the_data_is_found_through_the_binding_history()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Timelines();
        DateOnly? through;
        try
        {
            if (await store.GetReadinessAsync(ct) != HistoryReadiness.Ready)
            {
                Assert.Skip("the binding history is not ready on this ClickHouse");
                return;
            }

            through = await store.DataThroughAsync(ct);
        }
        catch (HttpRequestException ex)
        {
            Assert.Skip($"no ClickHouse at localhost:18123: {ex.Message}");
            return;
        }

        var reader = new ClickHouseBindingChangeReader(
            Options.Create(new ClickHouseOptions { ConnectionString = Connection("sqm_app") }), new Factory());
        var change = (await reader.ReadAsync(through!.Value, null, 1, ct)).Changes.Single();

        var watch = Stopwatch.StartNew();
        var timeline = await store.GetAsync(TimelineCentre.Msisdn, change.Msisdn.ToString(System.Globalization.CultureInfo.InvariantCulture), 200, ct);
        Write($"lookup of a number from {through:yyyy-MM-dd}: {timeline.Bindings.Count} binding(s) in {watch.ElapsedMilliseconds} ms");

        Assert.Contains(timeline.Bindings, b => b.Imsi == change.Imsi && b.Imei == change.Imei);
    }

    private static (ulong, ulong, string, int, bool) Key(DailyBindingChange c) => (c.Msisdn, c.Imsi, c.Imei, c.Sequence, c.Added);

    /// <summary>The event log's own order: (msisdn, imsi, imei, seq), the IMEI compared byte by byte as ClickHouse does.</summary>
    private static int Compare(DailyBindingChange a, DailyBindingChange b)
    {
        var c = a.Msisdn.CompareTo(b.Msisdn);
        if (c != 0)
        {
            return c;
        }

        c = a.Imsi.CompareTo(b.Imsi);
        if (c != 0)
        {
            return c;
        }

        c = string.CompareOrdinal(a.Imei, b.Imei);
        return c != 0 ? c : a.Sequence.CompareTo(b.Sequence);
    }
}
