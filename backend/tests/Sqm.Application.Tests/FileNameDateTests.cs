using Sqm.Application.DataImport;

namespace Sqm.Application.Tests;

/// <summary>
/// The date a file's name carries, however the name writes it - and no date at all where the name could mean
/// two. A daily file's date comes from nowhere else, so a wrong reading files a day's changes under another day.
/// </summary>
public sealed class FileNameDateTests
{
    private static DateOnly D(int year, int month, int day) => new(year, month, day);

    [Theory]
    // The names the sources really use.
    [InlineData("daily_subs_device_sim_info_2026-10-17.csv", 2026, 10, 17)]
    [InlineData("DeviceDatabase_TAC4Oct2026.csv", 2026, 10, 4)]
    [InlineData("DeviceDatabaseTAC09Nov2026.csv", 2026, 11, 9)]
    [InlineData("DeviceDatabaseTAC09Nov2026.rar", 2026, 11, 9)]
    [InlineData("DeviceDatabase_TAC16Sep2026.repaired.csv", 2026, 9, 16)]
    // Year first.
    [InlineData("subs_2026_10_17.csv", 2026, 10, 17)]
    [InlineData("subs.2026.10.17.csv.gz", 2026, 10, 17)]
    [InlineData("subs 2026 10 17.csv", 2026, 10, 17)]
    [InlineData("subs_20261017.csv", 2026, 10, 17)]
    [InlineData("subs_2026-1-7.csv", 2026, 1, 7)]
    [InlineData("export_20261017235959.csv", 2026, 10, 17)]
    [InlineData("export_202610172359.csv", 2026, 10, 17)]
    [InlineData("export_20261017_235959.csv", 2026, 10, 17)]
    [InlineData("export_2026-10-17T23-59-59.csv", 2026, 10, 17)]
    // A month by name, in any order.
    [InlineData("TAC 9-Nov-2026.csv", 2026, 11, 9)]
    [InlineData("TAC 9 November 2026.csv", 2026, 11, 9)]
    [InlineData("tac_1st_september_2026.csv", 2026, 9, 1)]
    [InlineData("TAC_Nov-09-2026.csv", 2026, 11, 9)]
    [InlineData("TAC November 9, 2026.csv", 2026, 11, 9)]
    [InlineData("TAC_Nov092026.csv", 2026, 11, 9)]
    [InlineData("TAC_2026Nov09.csv", 2026, 11, 9)]
    [InlineData("TAC_2026-Sept-3.csv", 2026, 9, 3)]
    [InlineData("tac_03MAY2026.CSV", 2026, 5, 3)]
    // Day and month as numbers: exact when only one of them can be the month, or they are equal.
    [InlineData("subs_17-10-2026.csv", 2026, 10, 17)]
    [InlineData("subs_17.10.2026.csv", 2026, 10, 17)]
    [InlineData("subs_17102026.csv", 2026, 10, 17)]
    [InlineData("subs_10-17-2026.csv", 2026, 10, 17)]
    [InlineData("subs_05-05-2026.csv", 2026, 5, 5)]
    [InlineData("subs_20102026.csv", 2026, 10, 20)]
    // Written twice the same way, and an ambiguous form settled by an exact one beside it.
    [InlineData("subs_2026-10-17_2026-10-17.csv", 2026, 10, 17)]
    [InlineData("subs_03-04-2026_(2026-04-03).csv", 2026, 4, 3)]
    // In a folder of an archive: the folder is not the file's name.
    [InlineData("2026-01-01/subs_2026-10-17.csv", 2026, 10, 17)]
    public void A_name_that_can_mean_one_date_is_read_as_that_date(string name, int year, int month, int day)
    {
        var reading = FileNameDate.Read(name);

        Assert.Equal(FileNameDateKind.Exact, reading.Kind);
        Assert.Equal(D(year, month, day), reading.Date);
        Assert.Equal([D(year, month, day)], reading.Candidates);
        Assert.Equal(D(year, month, day), FileNameDate.ExactOf(name));
    }

    [Theory]
    [InlineData("subs_03-04-2026.csv")]
    [InlineData("subs_03.04.2026.csv")]
    [InlineData("subs_03042026.csv")]
    [InlineData("subs_3-4-2026.csv")]
    public void Day_and_month_that_can_swap_are_ambiguous_and_both_are_offered(string name)
    {
        var reading = FileNameDate.Read(name);

        Assert.Equal(FileNameDateKind.Ambiguous, reading.Kind);
        Assert.Null(reading.Date);
        Assert.Equal([D(2026, 3, 4), D(2026, 4, 3)], reading.Candidates);
        // Never guessed: a wrong day would take the file's changes with it.
        Assert.Null(FileNameDate.ExactOf(name));
    }

    [Theory]
    // The initial dump is a range, not a day.
    [InlineData("dump_subs_device_sim_info_20251227_20260125.csv", "2025-12-27,2026-01-25")]
    [InlineData("subs_2026-10-17_exported_2026-10-18.csv", "2026-10-17,2026-10-18")]
    // An exact date and an ambiguous one that cannot mean it.
    [InlineData("subs_2026-10-17_03-04-2026.csv", "2026-03-04,2026-04-03,2026-10-17")]
    public void Two_different_dates_in_one_name_are_not_a_day(string name, string candidates)
    {
        var reading = FileNameDate.Read(name);

        Assert.Equal(FileNameDateKind.Several, reading.Kind);
        Assert.Null(reading.Date);
        Assert.Equal(candidates, string.Join(',', reading.Candidates.Select(c => c.ToString("yyyy-MM-dd"))));
        Assert.Null(FileNameDate.ExactOf(name));
    }

    [Theory]
    [InlineData("DeviceDatabase.csv")]
    [InlineData("subs.csv")]
    [InlineData("")]
    [InlineData(null)]
    // A two-digit year has three plausible readings.
    [InlineData("subs_17-10-26.csv")]
    [InlineData("subs_171026.csv")]
    // Not a date: no thirteenth month, no 30 February, no day at all.
    [InlineData("subs_2026-13-01.csv")]
    [InlineData("subs_2026-02-30.csv")]
    [InlineData("subs_Nov2026.csv")]
    [InlineData("subs_2026-10.csv")]
    // A Jalali date is not read (the owner's choice): it waits for a person.
    [InlineData("subs_1405-08-19.csv")]
    [InlineData("subs_14050819.csv")]
    // Digits that are part of a longer number are not a date.
    [InlineData("subs_920261017.csv")]
    [InlineData("subs_2026101.csv")]
    [InlineData("imei_35085748000001.csv")]
    // A month's letters inside another word are not a month.
    [InlineData("remark09-2026.csv")]
    [InlineData("summar152026.csv")]
    // Mixed separators are a version number or an accident, not a date.
    [InlineData("build_2026-10.17.csv")]
    public void A_name_with_no_date_in_it_says_so(string? name)
    {
        Assert.Equal(FileNameDateKind.None, FileNameDate.Read(name).Kind);
        Assert.Null(FileNameDate.ExactOf(name));
    }

    [Fact]
    public void Every_month_is_read_by_its_short_and_its_full_name()
    {
        string[] names =
        [
            "Jan", "January", "Feb", "February", "Mar", "March", "Apr", "April", "May", "Jun", "June", "Jul", "July",
            "Aug", "August", "Sep", "Sept", "September", "Oct", "October", "Nov", "November", "Dec", "December",
        ];
        int[] months = [1, 1, 2, 2, 3, 3, 4, 4, 5, 6, 6, 7, 7, 8, 8, 9, 9, 9, 10, 10, 11, 11, 12, 12];

        for (var i = 0; i < names.Length; i++)
        {
            Assert.Equal(D(2026, months[i], 7), FileNameDate.ExactOf($"TAC07{names[i]}2026.csv"));
            Assert.Equal(D(2026, months[i], 7), FileNameDate.ExactOf($"TAC_{names[i].ToUpperInvariant()} 7, 2026.csv"));
        }
    }

    [Fact]
    public void A_very_long_name_is_read_in_bounded_time()
    {
        // The name comes from outside. Nothing here may take a pattern past its timeout, or past a moment.
        var name = new string('1', 20_000) + "-" + new string('a', 20_000) + "_2026-10-17.csv";
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var reading = FileNameDate.Read(name);

        Assert.True(watch.ElapsedMilliseconds < 2_000, $"took {watch.ElapsedMilliseconds} ms");
        Assert.True(reading.Kind is FileNameDateKind.Exact or FileNameDateKind.None);
    }
}
