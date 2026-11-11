using System.Globalization;

namespace Sqm.Application.DataImport;

/// <summary>Who said what a file's date is.</summary>
public enum FileDateSource
{
    /// <summary>The file's own name.</summary>
    Name,

    /// <summary>The name of the archive it arrived in, as that archive's only file.</summary>
    Archive,

    /// <summary>A person, asked because the names did not settle it.</summary>
    Person,
}

/// <summary>Whether a file has its date, and if not, why not.</summary>
public enum FileDateState
{
    /// <summary>One date, known.</summary>
    Settled,

    /// <summary>The name writes day and month so that they can be swapped.</summary>
    Ambiguous,

    /// <summary>The name holds more than one date.</summary>
    Several,

    /// <summary>The name holds no date.</summary>
    Missing,

    /// <summary>The name's date has not happened yet, so it is not believed.</summary>
    Future,
}

/// <summary>What is known about a file's date.</summary>
/// <param name="State">Settled, or why it is not.</param>
/// <param name="Date">The date, when settled.</param>
/// <param name="Source">Who said so, when settled now; null for a date read back from the record.</param>
/// <param name="Candidates">
/// What a person may choose from, earliest first. Empty when the names offer nothing: then any day
/// that has happened may be given.
/// </param>
public sealed record FileDateDecision(
    FileDateState State, DateOnly? Date, FileDateSource? Source, IReadOnlyList<DateOnly> Candidates)
{
    /// <summary>Whether the file can go on to be imported.</summary>
    public bool IsSettled => State == FileDateState.Settled;

    /// <summary>A settled date.</summary>
    public static FileDateDecision Of(DateOnly date, FileDateSource source) =>
        new(FileDateState.Settled, date, source, [date]);

    /// <summary>A date already recorded on the file: settled, by whoever settled it then.</summary>
    public static FileDateDecision Recorded(DateOnly date) =>
        new(FileDateState.Settled, date, null, [date]);

    /// <summary>Why the file waits, for the person who will be asked.</summary>
    public string Explain(string fileName) => State switch
    {
        FileDateState.Settled => $"{fileName} is dated {FileDate.Text(Date!.Value)}.",
        FileDateState.Ambiguous =>
            $"The date in {fileName} can be read two ways: {FileDate.Text(Candidates[0])} or "
            + $"{FileDate.Text(Candidates[^1])}. It waits for someone to say which.",
        FileDateState.Several =>
            $"{fileName} holds more than one date ({string.Join(", ", Candidates.Select(FileDate.Text))}). "
            + "It waits for someone to say which is its own.",
        FileDateState.Future =>
            $"The date in {fileName} has not happened yet, so it is not taken as the file's date. "
            + "It waits for someone to give it.",
        _ => $"There is no date in the name {fileName}. It waits for someone to give it.",
    };
}

/// <summary>
/// Decides a file's date: from its own name, from the archive it arrived in, or from a person.
/// </summary>
/// <remarks>
/// <para>
/// <b>What the date is.</b> For a daily file it is the day the file describes, and the file says it
/// nowhere but in its name. For a snapshot - the GSMA catalogue - it is the dataset's own date: its
/// label, and what its age is measured from. Either way a wrong date is believed by everything after
/// it, so it is never guessed (the owner's choice, 2026-11-10): a name that can mean two dates, or
/// none, leaves the file stored and waiting until a person says.
/// </para>
/// <para>
/// <b>The archive's name.</b> GSMA delivers <c>DeviceDatabaseTAC09Nov2026.rar</c>, and the file
/// inside is <c>DeviceDatabase.csv</c>: the date is on the wrapping. So a file that is the only one
/// in its archive may take the archive's date - when its own name gives none, or gives several that
/// the archive's date is one of. An archive of many files dates none of them: its name is about
/// the set.
/// </para>
/// <para>
/// <b>Not the future.</b> More ways of writing a date are read now, so more things that are not a
/// date can look like one. A date later than tomorrow is not a day any file can describe, and is
/// not taken: tomorrow is allowed because the server keeps UTC and the files are named in Tehran.
/// </para>
/// </remarks>
public static class FileDate
{
    /// <summary>A date as every message writes it: 2026-11-09, whatever the machine's calendar.</summary>
    public static string Text(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>The last day a file's name may claim, seen from <paramref name="today"/> (UTC).</summary>
    public static DateOnly Latest(DateOnly today) => today.AddDays(1);

    /// <summary>What the names say.</summary>
    /// <param name="fileName">The file's own name.</param>
    /// <param name="archiveName">The archive it arrived in, if it did.</param>
    /// <param name="onlyFileInArchive">Whether it is the one file that archive holds.</param>
    /// <param name="today">Today, in UTC.</param>
    public static FileDateDecision Decide(
        string? fileName, string? archiveName, bool onlyFileInArchive, DateOnly today)
    {
        var own = FileNameDate.Read(fileName);

        if (own is { Kind: FileNameDateKind.Exact, Date: { } named })
        {
            return NotAfter(named, FileDateSource.Name, today);
        }

        var wrapping = onlyFileInArchive ? FileNameDate.Read(archiveName) : FileNameDateReading.Nothing;

        if (wrapping is { Kind: FileNameDateKind.Exact, Date: { } wrapped }
            && (own.Kind == FileNameDateKind.None || own.Candidates.Contains(wrapped)))
        {
            return NotAfter(wrapped, FileDateSource.Archive, today);
        }

        // Unsettled. What the file's own name offers is what a person chooses from; a name that
        // offers nothing leaves the archive's, if it has any.
        var offered = own.Kind == FileNameDateKind.None ? wrapping : own;
        var candidates = offered.Candidates.Where(c => c <= Latest(today)).ToList();

        return offered.Kind switch
        {
            FileNameDateKind.None => new FileDateDecision(FileDateState.Missing, null, null, []),
            // Every date it could mean is in the future: nothing is left to choose from.
            _ when candidates.Count == 0 => new FileDateDecision(FileDateState.Future, null, null, []),
            FileNameDateKind.Ambiguous => new FileDateDecision(FileDateState.Ambiguous, null, null, candidates),
            _ => new FileDateDecision(FileDateState.Several, null, null, candidates),
        };
    }

    /// <summary>Takes a person's word for the date, where the names left room for it.</summary>
    /// <param name="names">What the names said.</param>
    /// <param name="said">The date the person gave.</param>
    /// <param name="today">Today, in UTC.</param>
    /// <param name="problem">Why the date is refused, when it is.</param>
    /// <returns>The settled date, or <see langword="null"/> with <paramref name="problem"/> set.</returns>
    public static FileDateDecision? Accept(
        FileDateDecision names, DateOnly said, DateOnly today, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(names);
        problem = null;

        if (names is { IsSettled: true, Date: { } known })
        {
            if (said == known)
            {
                return names;
            }

            // A name that says one date plainly is not overruled by a typed one: if the name is
            // wrong, renaming the file leaves a record of that, and a typed date leaves none.
            problem = $"The name already says {Text(known)}. If that is wrong, rename the file and send it again.";
            return null;
        }

        if (said > Latest(today))
        {
            problem = $"{Text(said)} has not happened yet.";
            return null;
        }

        if (names.Candidates.Count > 0 && !names.Candidates.Contains(said))
        {
            problem = $"The name can mean {string.Join(" or ", names.Candidates.Select(Text))}, not {Text(said)}.";
            return null;
        }

        return FileDateDecision.Of(said, FileDateSource.Person);
    }

    private static FileDateDecision NotAfter(DateOnly date, FileDateSource source, DateOnly today) =>
        date > Latest(today)
            ? new FileDateDecision(FileDateState.Future, null, null, [])
            : FileDateDecision.Of(date, source);
}

/// <summary>What the platform knows about each source's dates.</summary>
public static class ImportSources
{
    /// <summary>
    /// Whether a job of this source carries the file's date as its business date.
    /// </summary>
    /// <remarks>
    /// A daily file's job does: the queue imports days in order, and compares the dates jobs carry.
    /// A snapshot's does not, and must not: its date is a label, and a job with a business date
    /// would wait behind every earlier dataset that failed. Its date is kept on the file.
    /// </remarks>
    public static bool JobCarriesTheDate(string sourceCode) =>
        !string.Equals(sourceCode, "TAC", StringComparison.OrdinalIgnoreCase);
}
