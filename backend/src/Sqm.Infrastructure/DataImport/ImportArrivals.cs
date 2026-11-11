using System.IO.Compression;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sqm.Application.DataImport;

namespace Sqm.Infrastructure.DataImport;

/// <summary>One file that came out of an arrival, and what became of it.</summary>
/// <param name="FileName">Its own name.</param>
/// <param name="Stored">Where its bytes went, and their hash.</param>
/// <param name="Intake">Queued, already imported, or waiting for its date.</param>
public sealed record ArrivedOutcome(string FileName, StoredFile Stored, IntakeResult Intake);

/// <summary>Something an archive held that was not taken.</summary>
/// <param name="Name">Its path inside the archive.</param>
/// <param name="Reason">Why, in words for the person who sent it.</param>
public sealed record SkippedEntry(string Name, string Reason);

/// <summary>What became of something that arrived.</summary>
/// <param name="FileName">The name it arrived under.</param>
/// <param name="Kind">What it turned out to be.</param>
/// <param name="Files">The files taken from it: one for a plain or gzip file, any number for an archive.</param>
/// <param name="Skipped">What an archive held besides.</param>
public sealed record ArrivalReport(
    string FileName, FileKind Kind, IReadOnlyList<ArrivedOutcome> Files, IReadOnlyList<SkippedEntry> Skipped)
{
    /// <summary>Whether it was an archive of files, as against one file however compressed.</summary>
    public bool IsArchive => Kind is FileKind.Zip or FileKind.Rar;
}

/// <summary>
/// Takes whatever arrives - a CSV, or a CSV packed as GZ, ZIP or RAR - and hands the files inside
/// to the intake (ADR-025).
/// </summary>
/// <remarks>
/// <para>
/// <b>What is stored is the file inside, never the packing.</b> The hash that says whether a file
/// has been seen before is the hash of what is stored, so the same catalogue sent as a CSV, as a
/// <c>.gz</c> carried to the server in pieces, and as GSMA's own <c>.rar</c> is one file and is
/// imported once. The archive itself is not kept: it can be made again from what is.
/// </para>
/// <para>
/// <b>Unpacked here, in the request, and not by the worker.</b> Measured on the real files: the
/// catalogue's archive unpacks in 1.0 s within 71 MiB, a 570 MB daily file in 2.2 s (ZIP) to 6.4 s
/// (RAR) within 70 MiB. That is short beside the upload it follows, it needs no second queue, and
/// the answer - which files were found, which wait for a date - reaches the person who is still
/// looking at the page.
/// </para>
/// <para>
/// <b>An archive is taken whole or not at all.</b> Every file is unpacked and stored first; only
/// when all of them have unpacked sound is any registered. One damaged file refuses the archive
/// and removes what was unpacked before it.
/// </para>
/// <para>
/// <b>An archive is somebody else's program for what to write to this disk.</b> So: only files
/// named <c>.csv</c> are taken, by their own name and never by their path; nothing inside is
/// unpacked a second time; a password or a missing volume is refused before anything is read; the
/// number of files, the size of each, and their total are capped by what the archive declares and
/// again by what actually comes out; and room must be left on the volume afterwards.
/// </para>
/// </remarks>
public sealed partial class ImportArrivals
{
    private const string SpoolFolder = "_incoming";

    private static readonly TimeSpan SpoolAbandoned = TimeSpan.FromHours(24);

    private readonly IImportFileStore _files;
    private readonly IImportJobRepository _repository;
    private readonly ImportStorageOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<ImportArrivals> _logger;

    [LoggerMessage(EventId = 3150, Level = LogLevel.Information,
        Message = "Unpacked {Archive} ({Kind}): {Taken} file(s) taken, {Skipped} skipped, {Bytes} bytes in {Milliseconds} ms")]
    private partial void LogUnpacked(string archive, FileKind kind, int taken, int skipped, long bytes, long milliseconds);

    [LoggerMessage(EventId = 3151, Level = LogLevel.Warning, Message = "Refused {FileName}: {Reason}")]
    private partial void LogRefused(string fileName, string reason);

    public ImportArrivals(
        IImportFileStore files,
        IImportJobRepository repository,
        IOptions<ImportStorageOptions> options,
        TimeProvider clock,
        ILogger<ImportArrivals> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _files = files;
        _repository = repository;
        _options = options.Value;
        _clock = clock;
        _logger = logger;
    }

    private DateOnly Today => DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);

    /// <summary>Takes a file that is still arriving: an upload's body.</summary>
    /// <param name="sourceCode">Data source, upper case.</param>
    /// <param name="fileName">The name it arrives under.</param>
    /// <param name="content">Its bytes. Read once, forwards.</param>
    /// <param name="actor">Who sent it.</param>
    /// <param name="correlationId">The request's trace identifier.</param>
    /// <param name="saidDate">The date the sender gave for it, if they gave one.</param>
    /// <param name="ct">Stops the receiving. Once the bytes are here, nothing stops the rest.</param>
    /// <exception cref="FileRefusedException">It cannot be taken; the message is for the sender.</exception>
    public async Task<ArrivalReport> TakeAsync(
        string sourceCode,
        string fileName,
        Stream content,
        string actor,
        string? correlationId,
        DateOnly? saidDate,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);

        try
        {
            var peeked = await PeekedStream.OpenAsync(content, FileSignature.HeadBytes, ct).ConfigureAwait(false);
            var kind = KindOf(peeked.Head, fileName);

            switch (kind)
            {
                case FileKind.Plain:
                {
                    var capped = new CountingStream(peeked, read => CapArriving(read, fileName));
                    var stored = await _files.SaveAsync(sourceCode, fileName, capped, ct).ConfigureAwait(false);
                    return await OneFileAsync(kind, sourceCode, fileName, fileName, stored, actor, correlationId, saidDate)
                        .ConfigureAwait(false);
                }

                case FileKind.Gzip:
                {
                    var stored = await SaveGunzippedAsync(sourceCode, fileName, peeked, ct).ConfigureAwait(false);
                    return await OneFileAsync(kind, sourceCode, fileName, WithoutGz(fileName), stored, actor, correlationId, saidDate)
                        .ConfigureAwait(false);
                }

                default:
                {
                    // An archive's list of files is at its end, so it is read from a file. The copy
                    // is the packed size, and gone as soon as the archive is unpacked or refused.
                    var spool = await SpoolAsync(peeked, fileName, ct).ConfigureAwait(false);
                    try
                    {
                        return await UnpackAsync(kind, spool, sourceCode, fileName, actor, correlationId, saidDate)
                            .ConfigureAwait(false);
                    }
                    finally
                    {
                        TryDelete(spool);
                    }
                }
            }
        }
        catch (FileRefusedException ex)
        {
            LogRefused(fileName, ex.Message);
            throw;
        }
    }

    /// <summary>Takes a file that is already on this machine, whole.</summary>
    /// <param name="sourceCode">Data source, upper case.</param>
    /// <param name="fileName">The name it is known by.</param>
    /// <param name="path">Where it is.</param>
    /// <param name="adopt">
    /// Whether a plain file may be moved into the store instead of copied: true for a finished
    /// resumable upload, which sits on the store's own volume and belongs to nobody else.
    /// </param>
    /// <param name="actor">Who sent it.</param>
    /// <param name="correlationId">The request's trace identifier, when there is a request.</param>
    /// <param name="saidDate">The date the sender gave for it, if they gave one.</param>
    /// <param name="ct">Stops the reading of a plain or gzip file. An archive is unpacked regardless.</param>
    /// <exception cref="FileRefusedException">It cannot be taken; the message is for the sender.</exception>
    public async Task<ArrivalReport> TakeFileAsync(
        string sourceCode,
        string fileName,
        string path,
        bool adopt,
        string actor,
        string? correlationId,
        DateOnly? saidDate,
        CancellationToken ct)
    {
        try
        {
            FileKind kind;
            await using (var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                CapArriving(probe.Length, fileName);
                var peeked = await PeekedStream.OpenAsync(probe, FileSignature.HeadBytes, ct).ConfigureAwait(false);
                kind = KindOf(peeked.Head, fileName);
            }

            switch (kind)
            {
                case FileKind.Plain:
                {
                    StoredFile stored;
                    if (adopt)
                    {
                        stored = await _files.AdoptAsync(sourceCode, fileName, path, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        await using var file = new FileStream(
                            path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1 << 20, useAsync: true);
                        stored = await _files.SaveAsync(sourceCode, fileName, file, ct).ConfigureAwait(false);
                    }

                    return await OneFileAsync(kind, sourceCode, fileName, fileName, stored, actor, correlationId, saidDate)
                        .ConfigureAwait(false);
                }

                case FileKind.Gzip:
                {
                    StoredFile stored;
                    await using (var file = new FileStream(
                        path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1 << 20, useAsync: true))
                    {
                        stored = await SaveGunzippedAsync(sourceCode, fileName, file, ct).ConfigureAwait(false);
                    }

                    return await OneFileAsync(kind, sourceCode, fileName, WithoutGz(fileName), stored, actor, correlationId, saidDate)
                        .ConfigureAwait(false);
                }

                default:
                    return await UnpackAsync(kind, path, sourceCode, fileName, actor, correlationId, saidDate)
                        .ConfigureAwait(false);
            }
        }
        catch (FileRefusedException ex)
        {
            LogRefused(fileName, ex.Message);
            throw;
        }
    }

    /// <summary>What the first bytes say the file is; refuses an empty file and a packing that is not unpacked.</summary>
    private static FileKind KindOf(ReadOnlySpan<byte> head, string fileName)
    {
        if (head.Length == 0)
        {
            throw new FileRefusedException($"{fileName} is empty.");
        }

        var kind = FileSignature.Read(head, out var what);

        return kind == FileKind.Unsupported
            ? throw new FileRefusedException(
                $"{fileName} is {what}, which is not unpacked here. Send {FileSignature.Accepted}. Nothing was kept.")
            : kind;
    }

    private async Task<ArrivalReport> OneFileAsync(
        FileKind kind,
        string sourceCode,
        string arrivedAs,
        string fileName,
        StoredFile stored,
        string actor,
        string? correlationId,
        DateOnly? saidDate)
    {
        var intake = await ImportIntake
            .AcceptAsync(
                _repository, _files, new ArrivedFile(sourceCode, fileName, stored, SaidDate: saidDate),
                actor, correlationId, Today)
            .ConfigureAwait(false);

        return new ArrivalReport(arrivedAs, kind, [new ArrivedOutcome(fileName, stored, intake)], []);
    }

    /// <summary>Stores what a gzip holds, under its name without <c>.gz</c>.</summary>
    /// <remarks>
    /// So what is stored, and its hash, are exactly what an upload of the same CSV gives: a file
    /// sent both ways is recognised as one.
    /// </remarks>
    private async Task<StoredFile> SaveGunzippedAsync(
        string sourceCode, string fileName, Stream compressed, CancellationToken ct)
    {
        var name = WithoutGz(fileName);
        var trailer = new GzipTrailerStream(compressed);

        // Only bad packed data is the gzip's fault here: the stream under it may be a connection,
        // and a connection that breaks is not a damaged file.
        await using var unpacked = new ArchiveEntryStream(
            () => new GZipStream(trailer, CompressionMode.Decompress, leaveOpen: true), name,
            isDamage: ex => ex is InvalidDataException);
        var sum = new GzipSum();
        var guard = new UnpackGuard(_options, _options.MaxFileBytes, name);
        var counted = new CountingStream(unpacked, guard.Passed);

        var peeked = await PeekedStream.OpenAsync(new SummingStream(counted, sum), FileSignature.HeadBytes, ct)
            .ConfigureAwait(false);

        if (peeked.Head.Length == 0)
        {
            throw new FileRefusedException($"{fileName} unpacks to nothing.");
        }

        if (FileSignature.Read(peeked.Head, out var what) != FileKind.Plain)
        {
            throw new FileRefusedException(
                $"{fileName} holds {what ?? "another archive"}, not a CSV: a packing inside a packing is not "
                + $"unpacked. Send {FileSignature.Accepted}. Nothing was kept.");
        }

        var stored = await _files.SaveAsync(sourceCode, name, peeked, ct).ConfigureAwait(false);

        // The runtime's gzip reader stops quietly where the data stops, whether or not that is
        // where the file should end. A gzip ends with the length and CRC-32 of what it held, so
        // what came out is compared with them - and a file cut short in transfer is refused
        // instead of being imported as a short day.
        if (!trailer.Matches(sum))
        {
            await _files.DeleteAsync(stored.StoredPath, CancellationToken.None).ConfigureAwait(false);
            throw new FileRefusedException(
                $"{fileName} is damaged, or was not transferred whole: what it unpacks to does not match the "
                + "length and checksum it ends with. Nothing was kept. (A gzip made by joining several together "
                + "reads the same way: unpack it and send the CSV.)");
        }

        return stored;
    }

    /// <summary>Unpacks an archive on disk and hands every CSV in it to the intake.</summary>
    private async Task<ArrivalReport> UnpackAsync(
        FileKind kind,
        string path,
        string sourceCode,
        string archiveName,
        string actor,
        string? correlationId,
        DateOnly? saidDate)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();

        using var archive = ArchiveFile.Open(kind, path, archiveName);

        var (wanted, skipped) = ArchiveContents.Choose(archive.Entries);
        CheckDeclared(archive.Entries, wanted, skipped, archiveName, saidDate);

        var unpacked = new List<(ArchiveEntry Entry, StoredFile Stored)>();
        long total = 0;

        try
        {
            await archive.ReadAsync(wanted.ToHashSet(), async (entry, stream) =>
            {
                var peeked = await PeekedStream
                    .OpenAsync(stream, FileSignature.HeadBytes, CancellationToken.None).ConfigureAwait(false);

                if (peeked.Head.Length == 0)
                {
                    skipped.Add(new SkippedEntry(entry.Path, "It is empty."));
                    return;
                }

                if (FileSignature.Read(peeked.Head, out var what) != FileKind.Plain)
                {
                    skipped.Add(new SkippedEntry(
                        entry.Path, $"It is {what ?? "itself an archive"}: a packing inside a packing is not unpacked."));
                    return;
                }

                // What the archive declared was checked before anything was read. What comes out is
                // counted as well, because a declaration is only what the archive says of itself.
                var room = Math.Min(_options.MaxFileBytes, _options.MaxArchiveUnpackedBytes - total);
                var counted = new CountingStream(peeked, new UnpackGuard(_options, room, entry.Name).Passed);

                // Off any request's token: the archive is all here, and stopping part way would
                // only leave the work to be done again.
                var stored = await _files
                    .SaveAsync(sourceCode, entry.Name, counted, CancellationToken.None).ConfigureAwait(false);

                unpacked.Add((entry, stored));
                total += stored.SizeBytes;
            }).ConfigureAwait(false);
        }
        catch
        {
            // Whole or not at all: one file that would not unpack takes the others with it.
            foreach (var (_, stored) in unpacked)
            {
                await _files.DeleteAsync(stored.StoredPath, CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }

        if (unpacked.Count == 0)
        {
            throw new FileRefusedException(NothingToTake(archiveName, skipped));
        }

        var outcomes = new List<ArrivedOutcome>(unpacked.Count);

        for (var i = 0; i < unpacked.Count; i++)
        {
            var (entry, stored) = unpacked[i];

            try
            {
                var intake = await ImportIntake
                    .AcceptAsync(
                        _repository, _files,
                        new ArrivedFile(sourceCode, entry.Name, stored, archiveName, wanted.Count == 1, saidDate),
                        actor, correlationId, Today)
                    .ConfigureAwait(false);

                outcomes.Add(new ArrivedOutcome(entry.Name, stored, intake));
            }
            catch
            {
                // The files before this one are registered and stay. This one and the rest are
                // bytes nothing points at: removed, so the store holds no file without a record.
                for (var rest = i; rest < unpacked.Count; rest++)
                {
                    await _files
                        .DeleteAsync(unpacked[rest].Stored.StoredPath, CancellationToken.None).ConfigureAwait(false);
                }

                throw;
            }
        }

        var milliseconds = (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        LogUnpacked(archiveName, kind, outcomes.Count, skipped.Count, total, milliseconds);

        return new ArrivalReport(archiveName, kind, outcomes, skipped);
    }

    /// <summary>Refuses an archive for what it says of itself, before a byte of it is unpacked.</summary>
    private void CheckDeclared(
        IReadOnlyList<ArchiveEntry> entries,
        List<int> wanted,
        List<SkippedEntry> skipped,
        string archiveName,
        DateOnly? saidDate)
    {
        if (wanted.Count == 0)
        {
            throw new FileRefusedException(NothingToTake(archiveName, skipped));
        }

        if (wanted.Count > _options.MaxArchiveFiles)
        {
            throw new FileRefusedException(
                $"{archiveName} holds {wanted.Count:N0} CSV files; at most {_options.MaxArchiveFiles:N0} are taken "
                + "from one archive. Send it in smaller archives. Nothing was kept.");
        }

        if (saidDate is not null && wanted.Count > 1)
        {
            throw new FileRefusedException(
                $"A date was given, and {archiveName} holds {wanted.Count:N0} CSV files: one date cannot be all of "
                + "theirs. Send it without the date; each file is dated by its own name. Nothing was kept.");
        }

        long declared = 0;

        foreach (var index in wanted)
        {
            var entry = entries[index];

            if (entry.Size > _options.MaxFileBytes)
            {
                throw new FileRefusedException(
                    $"{entry.Name} in {archiveName} unpacks to {Size(entry.Size)}; the largest file taken is "
                    + $"{Size(_options.MaxFileBytes)}. Nothing was kept.");
            }

            declared += Math.Max(0, entry.Size);
        }

        if (declared > _options.MaxArchiveUnpackedBytes)
        {
            throw new FileRefusedException(
                $"{archiveName} unpacks to {Size(declared)}; at most {Size(_options.MaxArchiveUnpackedBytes)} is "
                + "unpacked from one archive. Send it in smaller archives. Nothing was kept.");
        }

        if (FreeSpace.TryGet(_options.RootPath) is { } free && free - declared < _options.MinFreeBytesAfterUnpack)
        {
            throw new FileRefusedException(
                $"{archiveName} unpacks to {Size(declared)} and the file store has {Size(free)} free: "
                + $"{Size(_options.MinFreeBytesAfterUnpack)} must stay free after it. Nothing was kept.");
        }
    }

    private static string NothingToTake(string archiveName, List<SkippedEntry> skipped)
    {
        const int Named = 5;

        var held = skipped.Count == 0
            ? "It is empty."
            : "It holds " + string.Join(", ", skipped.Take(Named).Select(s => s.Name))
              + (skipped.Count > Named ? $" and {skipped.Count - Named:N0} more." : ".");

        return $"{archiveName} holds no CSV file to import. {held} Nothing was kept.";
    }

    /// <summary>Stops a file that is larger than any file taken, at the byte where it becomes so.</summary>
    /// <remarks>
    /// The store has the same limit and enforces it too. Refused here first, it is a reason the
    /// sender is given; refused there, it would be a fault with no reason.
    /// </remarks>
    private void CapArriving(long bytes, string fileName)
    {
        if (bytes > _options.MaxFileBytes)
        {
            throw new FileRefusedException(
                $"{fileName} is larger than {Size(_options.MaxFileBytes)}, the largest file taken. Nothing was kept.");
        }
    }

    /// <summary>Copies an arriving archive to the store's own volume, to be read from there.</summary>
    private async Task<string> SpoolAsync(Stream content, string fileName, CancellationToken ct)
    {
        var folder = Path.Combine(Path.GetFullPath(_options.RootPath), SpoolFolder);
        Directory.CreateDirectory(folder);
        SweepAbandoned(folder);

        // No extension and no part of the client's name: nothing beside it can be mistaken for
        // another volume of the same archive.
        var path = Path.Combine(folder, Guid.NewGuid().ToString("N"));

        try
        {
            await using var file = new FileStream(
                path, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 1 << 20, useAsync: true);
            var counted = new CountingStream(content, read => CapArriving(read, fileName));

            await counted.CopyToAsync(file, 1 << 20, ct).ConfigureAwait(false);
        }
        catch
        {
            TryDelete(path);
            throw;
        }

        return path;
    }

    /// <summary>Removes copies a process left behind when it died between receiving and unpacking.</summary>
    private static void SweepAbandoned(string folder)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > SpoolAbandoned)
                {
                    TryDelete(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Litter is not a reason to refuse an upload.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Swept the next time something is spooled.
        }
    }

    private static string WithoutGz(string fileName) =>
        fileName.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) && fileName.Length > 3
            ? fileName[..^3]
            : fileName;

    internal static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 30):0.#} GB"),
        >= 1L << 20 => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 20):0.#} MB"),
        _ => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{bytes:N0} bytes"),
    };
}

/// <summary>
/// Watches a file as it unpacks: stops it at its limit, and stops it when the volume runs short.
/// </summary>
/// <remarks>
/// An archive says how large its files are, and that is checked before anything is read. This is
/// the check on what actually comes out - for a gzip, which says nothing until its last bytes, the
/// only one. The volume is looked at every 64 MiB rather than at every buffer.
/// </remarks>
internal sealed class UnpackGuard(ImportStorageOptions options, long limit, string name)
{
    private const long LookEvery = 64L << 20;

    private long _nextLook = LookEvery;

    /// <summary>Called with the running count of bytes unpacked.</summary>
    public void Passed(long unpacked)
    {
        if (unpacked > limit)
        {
            throw new FileRefusedException(
                $"{name} unpacks to more than {ImportArrivals.Size(Math.Max(0, limit))}, which is more than is "
                + "taken. Nothing was kept.");
        }

        if (unpacked < _nextLook)
        {
            return;
        }

        _nextLook = unpacked + LookEvery;

        if (FreeSpace.TryGet(options.RootPath) is { } free && free < options.MinFreeBytesAfterUnpack)
        {
            throw new FileRefusedException(
                $"The file store ran short of room while {name} was being unpacked: "
                + $"{ImportArrivals.Size(options.MinFreeBytesAfterUnpack)} must stay free. Nothing was kept.");
        }
    }
}

/// <summary>Which of an archive's files are data to import.</summary>
internal static class ArchiveContents
{
    /// <summary>The positions of the files to take, and why each of the others is left.</summary>
    /// <remarks>
    /// Only <c>.csv</c>, by name: an archive routinely carries a readme, a checksum file, and
    /// whatever the packing program adds on its own - macOS writes a <c>__MACOSX</c> folder whose
    /// entries are named after the real ones, <c>._DeviceDatabase.csv</c> and all, and are not
    /// them. A file that is named <c>.csv</c> and is not one is caught later, by its first bytes
    /// and then by the column contract.
    /// </remarks>
    public static (List<int> Wanted, List<SkippedEntry> Skipped) Choose(IReadOnlyList<ArchiveEntry> entries)
    {
        var wanted = new List<int>();
        var skipped = new List<SkippedEntry>();

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var name = entry.Name;

            if (name.Length == 0)
            {
                continue;
            }

            var addedByThePacker = name.StartsWith('.')
                || entry.Path.Split('/', '\\').Any(part => part == "__MACOSX")
                || name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase)
                || name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase);

            if (addedByThePacker)
            {
                skipped.Add(new SkippedEntry(entry.Path, "Not data: the packing program adds it."));
            }
            else if (name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            {
                wanted.Add(i);
            }
            else
            {
                skipped.Add(new SkippedEntry(entry.Path, "Not a .csv file."));
            }
        }

        return (wanted, skipped);
    }
}

/// <summary>How much room a volume has left.</summary>
internal static class FreeSpace
{
    /// <summary>Free bytes on the volume holding <paramref name="path"/>, or null when it cannot be told.</summary>
    /// <remarks>
    /// Null is "do not know", and is never treated as "none": a store whose room cannot be read is
    /// not for that reason refused an upload it may well have room for.
    /// </remarks>
    public static long? TryGet(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);

            // Windows names a drive by its root; elsewhere any path names the volume it is on.
            var drive = new DriveInfo(OperatingSystem.IsWindows() ? Path.GetPathRoot(full) ?? full : full);
            return drive.AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>The length and CRC-32 of what a gzip unpacked to.</summary>
internal sealed class GzipSum
{
    private uint _crc = 0xFFFFFFFF;

    /// <summary>Bytes unpacked.</summary>
    public long Length { get; private set; }

    /// <summary>Their CRC-32.</summary>
    public uint Crc => _crc ^ 0xFFFFFFFF;

    public void Add(ReadOnlySpan<byte> data)
    {
        _crc = Crc32.Append(_crc, data);
        Length += data.Length;
    }
}

/// <summary>Passes a stream through, summing what is read from it.</summary>
internal sealed class SummingStream(Stream inner, GzipSum sum) : Stream
{
    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => sum.Length;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var got = inner.Read(buffer);
        sum.Add(buffer[..got]);
        return got;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var got = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        sum.Add(buffer.Span[..got]);
        return got;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>
/// Passes a gzip through to its decompressor, remembering the last eight bytes that went by.
/// </summary>
/// <remarks>
/// A gzip ends with the CRC-32 of what it held and its length modulo 2^32 (RFC 1952). They are the
/// file's own statement of what should have come out, and the only one there is: the decompressor
/// in the runtime does not report whether it reached them.
/// </remarks>
internal sealed class GzipTrailerStream(Stream inner) : Stream
{
    private readonly byte[] _tail = new byte[8];
    private long _passed;

    /// <summary>Whether what was unpacked is what the gzip's last eight bytes say it held.</summary>
    public bool Matches(GzipSum sum)
    {
        ArgumentNullException.ThrowIfNull(sum);

        if (_passed < 18)
        {
            // Shorter than a header and a trailer: there is no statement to compare with.
            return false;
        }

        var crc = BitConverter.ToUInt32(_tail, 0);
        var length = BitConverter.ToUInt32(_tail, 4);
        return crc == sum.Crc && length == unchecked((uint)sum.Length);
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _passed;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var got = inner.Read(buffer);
        Remember(buffer[..got]);
        return got;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var got = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        Remember(buffer.Span[..got]);
        return got;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private void Remember(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return;
        }

        _passed += data.Length;

        if (data.Length >= 8)
        {
            data[^8..].CopyTo(_tail);
            return;
        }

        // Fewer than eight new bytes: the oldest make room for them.
        _tail.AsSpan(data.Length).CopyTo(_tail);
        data.CopyTo(_tail.AsSpan(8 - data.Length));
    }
}
