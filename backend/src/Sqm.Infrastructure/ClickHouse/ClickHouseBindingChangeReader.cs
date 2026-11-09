using System.Globalization;
using Microsoft.Extensions.Options;
using Sqm.Application.Integration;

namespace Sqm.Infrastructure.ClickHouse;

/// <summary>
/// One day's binding adds and removes, read from the event log for incremental synchronisation (ADR-024).
/// </summary>
/// <remarks>
/// <para>
/// <c>binding_event</c> is partitioned by day and ordered by <c>(msisdn, imsi, imei, seq)</c>, so a page is a
/// partition read in key order from where the last one stopped: keyset paging, never an offset, so page
/// ten thousand costs what page one does. The day prunes every other partition; <c>msisdn &gt;=</c> lets the
/// primary index skip the granules a previous page already covered.
/// </para>
/// <para>
/// A row with no handset is left out, as from every count (ADR-020): it is not a binding.
/// </para>
/// </remarks>
public sealed class ClickHouseBindingChangeReader : IBindingChangeReader
{
    private readonly ClickHouseJsonQuery _query;

    /// <summary>Creates the reader over the analytics connection.</summary>
    public ClickHouseBindingChangeReader(IOptions<ClickHouseOptions> options, IHttpClientFactory httpClientFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        _query = new ClickHouseJsonQuery(options.Value.ConnectionString, httpClientFactory);
    }

    /// <inheritdoc />
    public async Task<BindingChangePage> ReadAsync(DateOnly day, BindingChangeCursor? after, int limit, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["day"] = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["rows"] = (limit + 1).ToString(CultureInfo.InvariantCulture),
        };

        var resume = string.Empty;
        if (after is not null)
        {
            parameters["fromMsisdn"] = after.Msisdn.ToString(CultureInfo.InvariantCulture);
            parameters["fromImsi"] = after.Imsi.ToString(CultureInfo.InvariantCulture);
            parameters["fromImei"] = after.Imei;
            parameters["fromSeq"] = after.Sequence.ToString(CultureInfo.InvariantCulture);
            resume = """
                AND msisdn >= {fromMsisdn:UInt64}
                AND (msisdn, imsi, imei, seq) > ({fromMsisdn:UInt64}, {fromImsi:UInt64}, {fromImei:String}, {fromSeq:UInt16})
                """;
        }

        var noHandset = AnalyticsSchema.NoHandset("imei", "substring(imei, 1, 8)", "tac");
        var sql = $$"""
            SELECT data_date, seq, msisdn, imsi, imei, label = 'add'
            FROM binding_event
            WHERE data_date = {day:Date}
              {{resume}}
              AND NOT {{noHandset}}
            ORDER BY msisdn, imsi, imei, seq
            LIMIT {rows:UInt32}
            """;

        // A day is about eight million events; one page reads from its key onwards and stops at the limit.
        var result = await _query.ExecuteAsync(
            sql, parameters, ct, new QueryBudget(MaxRowsToRead: 50_000_000, MaxExecutionSeconds: 30, MaxResultRows: limit + 1))
            .ConfigureAwait(false);

        var changes = new List<DailyBindingChange>(Math.Min(result.Rows.Count, limit));
        foreach (var row in result.Rows.Take(limit))
        {
            var imei = ClickHouseJsonResult.Text(row, 4) ?? string.Empty;
            changes.Add(new DailyBindingChange(
                ClickHouseJsonResult.Date(row, 0) ?? day,
                ClickHouseJsonResult.Int32(row, 1),
                ClickHouseJsonResult.UInt64(row, 2),
                ClickHouseJsonResult.UInt64(row, 3),
                imei,
                imei.Length == 14 ? imei[..8] : null,
                ClickHouseJsonResult.Int32(row, 5) == 1));
        }

        var next = result.Rows.Count > limit && changes.Count > 0
            ? new BindingChangeCursor(changes[^1].Msisdn, changes[^1].Imsi, changes[^1].Imei, changes[^1].Sequence)
            : null;

        return new BindingChangePage(changes, next);
    }
}
