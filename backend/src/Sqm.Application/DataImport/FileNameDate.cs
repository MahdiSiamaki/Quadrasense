using System.Globalization;
using System.Text.RegularExpressions;

namespace Sqm.Application.DataImport;

/// <summary>How sure a file name is about the date it carries.</summary>
public enum FileNameDateKind
{
    /// <summary>No date in the name.</summary>
    None,

    /// <summary>One date, readable one way only.</summary>
    Exact,

    /// <summary>One date written so that day and month can be swapped: <c>03-04-2026</c>.</summary>
    Ambiguous,

    /// <summary>More than one date: a range, as the initial dump's name is, or a file dated twice.</summary>
    Several,
}

/// <summary>What a file name says about its date.</summary>
/// <param name="Kind">How sure it is.</param>
/// <param name="Date">The date, when <paramref name="Kind"/> is <see cref="FileNameDateKind.Exact"/>.</param>
/// <param name="Candidates">Every date the name could mean, earliest first; one for an exact reading.</param>
public sealed record FileNameDateReading(FileNameDateKind Kind, DateOnly? Date, IReadOnlyList<DateOnly> Candidates)
{
    /// <summary>A name with no date in it.</summary>
    public static FileNameDateReading Nothing { get; } = new(FileNameDateKind.None, null, []);
}

/// <summary>
/// Reads the date out of a file's name, however the name writes it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it has to be careful.</b> A daily file's date comes from nowhere else: the file is a list of
/// bindings added and removed, with no day in it. A date read wrongly files a whole day's changes under
/// another day, and nothing downstream can notice. So a name is read only where it can mean one date
/// (the owner's choice, 2026-11-10): where it could mean two, or holds none, the reading says so and the
/// file waits for a person rather than for a guess.
/// </para>
/// <para>
/// <b>What is read</b>, in the Gregorian calendar, years 2000 to 2099, with <c>-</c>, <c>_</c>, <c>.</c>
/// or a space between the parts, or nothing:
/// </para>
/// <list type="bullet">
/// <item>year first: <c>2026-10-17</c>, <c>2026_10_17</c>, <c>20261017</c>, and a timestamp's date,
/// <c>20261017235959</c>;</item>
/// <item>a month by name, in any order: <c>09Nov2026</c>, <c>9-Nov-2026</c>, <c>1st September 2026</c>,
/// <c>Nov-09-2026</c>, <c>November 9, 2026</c>, <c>2026Nov09</c>;</item>
/// <item>day and month as numbers before the year: <c>17-10-2026</c>, <c>17102026</c>, and
/// <c>10-17-2026</c> - exact whenever one of the two numbers is over twelve or the two are equal.</item>
/// </list>
/// <para>
/// <b>What is not.</b> <c>03-04-2026</c> is 3 April or 4 March: ambiguous, both offered. Two different
/// dates in one name - the initial dump's <c>…_20251227_20260125</c>, or a file dated and re-dated on
/// export - are not a day. A two-digit year is not read at all: <c>17-10-26</c> has three plausible
/// readings. Nor is a Jalali date (the owner's choice): <c>1405-08-19</c> waits for a person.
/// </para>
/// <para>
/// A month name is taken only where it stands as a word or against digits - <c>Nov09</c>, <c>9Nov</c>,
/// <c>_nov_</c> - never from inside another word, and only with a day and a four-digit year beside it.
/// </para>
/// </remarks>
public static partial class FileNameDate
{
    private const string Month =
        "Jan(?:uary)?|Feb(?:ruary)?|Mar(?:ch)?|Apr(?:il)?|May|June?|July?|Aug(?:ust)?|Sep(?:t(?:ember)?)?"
        + "|Oct(?:ober)?|Nov(?:ember)?|Dec(?:ember)?";

    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    // A timeout on each, because a name arrives from outside: 200 ms is thousands of times what these need.
    private const int TimeoutMs = 200;

    /// <summary>2026-10-17, 2026_10_17, 2026.1.7 - the same separator twice - or 20261017 with none.</summary>
    [GeneratedRegex(@"(?<!\d)(?<y>20\d{2})(?<s>[-_. ])(?<m>\d{1,2})\k<s>(?<d>\d{1,2})(?!\d)|(?<!\d)(?<y>20\d{2})(?<m>\d{2})(?<d>\d{2})(?!\d)", Options, TimeoutMs)]
    private static partial Regex YearFirst { get; }

    /// <summary>20261017235959 or 202610172359: a timestamp, whose first eight digits are the date.</summary>
    [GeneratedRegex(@"(?<!\d)(?<y>20\d{2})(?<m>\d{2})(?<d>\d{2})(?:[01]\d|2[0-3])[0-5]\d(?:[0-5]\d)?(?!\d)", Options, TimeoutMs)]
    private static partial Regex Timestamp { get; }

    /// <summary>17-10-2026, 17.10.2026, 17102026, 10-17-2026: two numbers and the year; which is the day is decided after.</summary>
    [GeneratedRegex(@"(?<!\d)(?<a>\d{1,2})(?<s>[-_. ])(?<b>\d{1,2})\k<s>(?<y>20\d{2})(?!\d)|(?<!\d)(?<a>\d{2})(?<b>\d{2})(?<y>20\d{2})(?!\d)", Options, TimeoutMs)]
    private static partial Regex NumbersThenYear { get; }

    /// <summary>09Nov2026, 9-Nov-2026, 1st September 2026, TAC1Sep2026.</summary>
    [GeneratedRegex(@"(?<!\d)(?<d>\d{1,2})(?:st|nd|rd|th)?[-_. ]?(?<m>" + Month + @")[-_., ]{0,2}(?<y>20\d{2})(?!\d)", Options, TimeoutMs)]
    private static partial Regex DayMonthYear { get; }

    /// <summary>Nov-09-2026, November 9, 2026, Nov092026.</summary>
    [GeneratedRegex(@"(?<![A-Za-z])(?<m>" + Month + @")[-_. ]?(?<d>\d{1,2})(?:st|nd|rd|th)?[-_., ]{1,2}(?<y>20\d{2})(?!\d)|(?<![A-Za-z])(?<m>" + Month + @")(?<d>\d{2})(?<y>20\d{2})(?!\d)", Options, TimeoutMs)]
    private static partial Regex MonthDayYear { get; }

    /// <summary>2026Nov09, 2026-Nov-9.</summary>
    [GeneratedRegex(@"(?<!\d)(?<y>20\d{2})[-_. ]?(?<m>" + Month + @")[-_. ]?(?<d>\d{1,2})(?:st|nd|rd|th)?(?![0-9A-Za-z])", Options, TimeoutMs)]
    private static partial Regex YearMonthDay { get; }

    /// <summary>The one date a name carries, or null when it carries none, two, or one it does not settle.</summary>
    public static DateOnly? ExactOf(string? fileName) =>
        Read(fileName) is { Kind: FileNameDateKind.Exact, Date: { } date } ? date : null;

    /// <summary>What a name says about its date.</summary>
    /// <param name="fileName">The name as it arrived; a path's last part is what is read.</param>
    public static FileNameDateReading Read(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return FileNameDateReading.Nothing;
        }

        // An entry of an archive arrives with its folders: they are not the file's name.
        var name = fileName[(fileName.LastIndexOfAny(['/', '\\']) + 1)..];

        List<IReadOnlyList<DateOnly>> findings;
        try
        {
            findings = Find(name);
        }
        catch (RegexMatchTimeoutException)
        {
            // A name built to stall a pattern says nothing about a date.
            return FileNameDateReading.Nothing;
        }

        if (findings.Count == 0)
        {
            return FileNameDateReading.Nothing;
        }

        var certain = findings.Where(f => f.Count == 1).Select(f => f[0]).Distinct().ToList();
        var unsure = findings.Where(f => f.Count > 1).ToList();
        var all = findings.SelectMany(f => f).Distinct().Order().ToList();

        // One date read one way - and anything written ambiguously beside it can mean that same date.
        if (certain.Count == 1 && unsure.All(f => f.Contains(certain[0])))
        {
            return new FileNameDateReading(FileNameDateKind.Exact, certain[0], [certain[0]]);
        }

        // One date written ambiguously, perhaps more than once the same way.
        if (certain.Count == 0 && all.Count == 2 && unsure.All(f => f.Count == 2))
        {
            return new FileNameDateReading(FileNameDateKind.Ambiguous, null, all);
        }

        return new FileNameDateReading(FileNameDateKind.Several, null, all);
    }

    /// <summary>
    /// Every stretch of the name that reads as a date, each with the dates it can mean: one, or two
    /// when day and month can swap.
    /// </summary>
    /// <remarks>
    /// No stretch is read twice. Every pattern refuses to begin or end inside a run of digits, so
    /// <c>20261017235959</c> is a timestamp and is not also <c>2026-10-17</c> or <c>10 17 2359</c>.
    /// </remarks>
    private static List<IReadOnlyList<DateOnly>> Find(string name)
    {
        var found = new List<IReadOnlyList<DateOnly>>();

        void Add(params DateOnly?[] dates)
        {
            var real = dates.Where(d => d is not null).Select(d => d!.Value).Distinct().ToList();
            if (real.Count > 0)
            {
                found.Add(real);
            }
        }

        foreach (Match m in YearFirst.Matches(name))
        {
            Add(Date(m, "y", Number(m, "m"), Number(m, "d")));
        }

        foreach (Match m in Timestamp.Matches(name))
        {
            Add(Date(m, "y", Number(m, "m"), Number(m, "d")));
        }

        foreach (Match m in NumbersThenYear.Matches(name))
        {
            // Day first, and month first: each is offered if it is a real date. 17-10 has one, 03-04 two,
            // 05-05 the same one twice.
            var (a, b) = (Number(m, "a"), Number(m, "b"));
            Add(Date(m, "y", b, a), Date(m, "y", a, b));
        }

        foreach (var pattern in new[] { DayMonthYear, MonthDayYear, YearMonthDay })
        {
            foreach (Match m in pattern.Matches(name))
            {
                Add(Date(m, "y", MonthNumber(m.Groups["m"].Value), Number(m, "d")));
            }
        }

        return found;
    }

    private static int Number(Match match, string group) =>
        int.Parse(match.Groups[group].Value, NumberStyles.None, CultureInfo.InvariantCulture);

    private static DateOnly? Date(Match match, string yearGroup, int month, int day)
    {
        var year = Number(match, yearGroup);
        return month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(year, month)
            ? new DateOnly(year, month, day)
            : null;
    }

    private static int MonthNumber(string name) => name[..3].ToUpperInvariant() switch
    {
        "JAN" => 1, "FEB" => 2, "MAR" => 3, "APR" => 4, "MAY" => 5, "JUN" => 6,
        "JUL" => 7, "AUG" => 8, "SEP" => 9, "OCT" => 10, "NOV" => 11, _ => 12,
    };
}
