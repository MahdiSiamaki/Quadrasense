using System.IO.Compression;
using SharpCompress.Archives;
using SharpCompress.Archives.Rar;
using SharpCompress.Common;
using SharpCompress.Readers;
using Sqm.Application.DataImport;

namespace Sqm.Infrastructure.DataImport;

/// <summary>One file inside an archive.</summary>
/// <param name="Path">Its path inside the archive, as the archive writes it.</param>
/// <param name="Size">Its size unpacked, as the archive declares it.</param>
internal sealed record ArchiveEntry(string Path, long Size)
{
    /// <summary>The file's own name: the last part of its path. A path is never used as one.</summary>
    public string Name => Path[(Path.LastIndexOfAny(['/', '\\']) + 1)..];
}

/// <summary>
/// An archive on disk, read one file at a time.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two readers, each chosen by measurement</b> (ADR-025). On a real daily file of 570 MB packed
/// to 217 MB, the runtime's own ZIP reader unpacked it in 2.2 s and SharpCompress's in 3.9 s, both
/// within 40 MiB: so ZIP is read by the runtime, with no third-party code on that path. The runtime
/// cannot read RAR, which is what GSMA delivers; SharpCompress unpacked the catalogue of 9 November
/// (6.7 MB to 213 MB) in 1.0 s within 71 MiB, and a daily file packed as RAR in 6.4 s.
/// </para>
/// <para>
/// <b>Everything read from an archive is checked against the archive.</b> A file that does not
/// unpack to the size and checksum its archive recorded is refused as it is being read, before it
/// is stored. RAR's reader does this itself. The runtime's ZIP reader does not - one changed byte
/// came back without complaint, stored or deflated (measured 2026-11-11) - so it is done here.
/// </para>
/// </remarks>
internal abstract class ArchiveFile : IDisposable
{
    private const string Password =
        "The archive is protected by a password. Send it without one. Nothing was kept.";

    /// <summary>Every file in the archive, in the order it holds them. Folders are not listed.</summary>
    public abstract IReadOnlyList<ArchiveEntry> Entries { get; }

    /// <summary>
    /// Reads the chosen files, in the archive's order, handing each one's unpacked stream over.
    /// </summary>
    /// <param name="wanted">Positions in <see cref="Entries"/>.</param>
    /// <param name="read">Called once per chosen file; the stream is only good for that call.</param>
    public abstract Task ReadAsync(IReadOnlySet<int> wanted, Func<ArchiveEntry, Stream, Task> read);

    public abstract void Dispose();

    /// <summary>Opens an archive, or refuses it in words for whoever sent it.</summary>
    /// <param name="kind"><see cref="FileKind.Zip"/> or <see cref="FileKind.Rar"/>.</param>
    /// <param name="path">Where it is on disk.</param>
    /// <param name="name">The name it arrived under, for the refusal.</param>
    public static ArchiveFile Open(FileKind kind, string path, string name)
    {
        if (kind is not (FileKind.Zip or FileKind.Rar))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "not an archive");
        }

        try
        {
            return kind == FileKind.Zip ? new ZipFileReader(path) : new RarFileReader(path, name);
        }
        catch (CryptographicException ex)
        {
            // A RAR whose file names are encrypted too cannot even be listed.
            throw new FileRefusedException(Password, ex);
        }
        catch (Exception ex) when (IsDamage(ex))
        {
            throw new FileRefusedException(
                $"{name} could not be opened: it is damaged, or was not transferred whole. Nothing was kept.", ex);
        }
    }

    /// <summary>
    /// Whether an exception met while reading an archive from local disk is about the archive.
    /// </summary>
    /// <remarks>
    /// A decompressor fed damaged data fails in whatever way the damage leads it to - a format
    /// error, an index out of range, a null - and none of those is a fault of this platform. What
    /// is not the archive's: a refusal already worded, a cancellation, and running out of memory,
    /// which has its own words (<see cref="ArchiveEntryStream"/>).
    /// </remarks>
    internal static bool IsDamage(Exception ex) =>
        ex is not (FileRefusedException or OperationCanceledException or OutOfMemoryException);

    /// <summary>The runtime's ZIP reader.</summary>
    private sealed class ZipFileReader : ArchiveFile
    {
        private readonly ZipArchive _zip;
        private readonly List<ZipArchiveEntry> _files;

        public ZipFileReader(string path)
        {
            _zip = ZipFile.OpenRead(path);

            try
            {
                // A folder is an entry whose name ends in a slash: it has a path and no name.
                _files = [.. _zip.Entries.Where(e => e.Name.Length > 0)];

                if (_files.Any(e => e.IsEncrypted))
                {
                    throw new FileRefusedException(Password);
                }

                Entries = [.. _files.Select(e => new ArchiveEntry(e.FullName, e.Length))];
            }
            catch
            {
                _zip.Dispose();
                throw;
            }
        }

        public override IReadOnlyList<ArchiveEntry> Entries { get; }

        public override async Task ReadAsync(IReadOnlySet<int> wanted, Func<ArchiveEntry, Stream, Task> read)
        {
            for (var i = 0; i < _files.Count; i++)
            {
                if (!wanted.Contains(i))
                {
                    continue;
                }

                var file = _files[i];
                await using var stream = new ArchiveEntryStream(file.Open, Entries[i].Name, IsDamage, file.Crc32);
                await read(Entries[i], stream).ConfigureAwait(false);
            }
        }

        public override void Dispose() => _zip.Dispose();
    }

    /// <summary>SharpCompress's RAR reader, for RAR 4 and RAR 5.</summary>
    private sealed class RarFileReader : ArchiveFile
    {
        private readonly FileStream _file;
        private readonly IRarArchive _archive;
        private readonly List<IArchiveEntry> _files;

        public RarFileReader(string path, string name)
        {
            // From a stream, not from the path: given a path the library looks beside it for the
            // other volumes of a split archive and reads them too. What arrived is this one file.
            _file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1 << 16);

            try
            {
                _archive = RarArchive.OpenArchive(_file, new ReaderOptions { LeaveStreamOpen = true });
            }
            catch
            {
                _file.Dispose();
                throw;
            }

            try
            {
                _files = [.. _archive.Entries.Where(e => !e.IsDirectory)];

                if (_archive.IsEncrypted || _files.Any(e => e.IsEncrypted))
                {
                    throw new FileRefusedException(Password);
                }

                // One volume of several holds part of a file. The rest is in files this upload did
                // not bring, and part of a catalogue is not a smaller catalogue.
                if (_archive.Volumes.Count() > 1 || !_archive.IsComplete
                    || _files.Any(e => e.IsSplitAfter || !e.IsComplete))
                {
                    throw new FileRefusedException(
                        $"{name} is one part of an archive split into several. Pack it as a single archive and "
                        + "send that. Nothing was kept.");
                }

                Entries = [.. _files.Select(e => new ArchiveEntry(e.Key ?? string.Empty, e.Size))];
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public override IReadOnlyList<ArchiveEntry> Entries { get; } = [];

        public override async Task ReadAsync(IReadOnlySet<int> wanted, Func<ArchiveEntry, Stream, Task> read)
        {
            if (!_archive.IsSolid)
            {
                for (var i = 0; i < _files.Count; i++)
                {
                    if (!wanted.Contains(i))
                    {
                        continue;
                    }

                    var file = _files[i];
                    await using var stream = new ArchiveEntryStream(file.OpenEntryStream, Entries[i].Name, IsDamage);
                    await read(Entries[i], stream).ConfigureAwait(false);
                }

                return;
            }

            // A solid archive is one compressed stream: a file can only be reached through the
            // files before it, so they are walked in order and the unwanted ones passed over.
            IReader reader;
            try
            {
                reader = _archive.ExtractAllEntries();
            }
            catch (Exception ex) when (IsDamage(ex))
            {
                throw Unreadable(ex);
            }

            using (reader)
            {
                var index = -1;

                while (Next(reader))
                {
                    if (reader.Entry.IsDirectory)
                    {
                        continue;
                    }

                    index++;

                    if (index >= Entries.Count
                        || !string.Equals(reader.Entry.Key, Entries[index].Path, StringComparison.Ordinal))
                    {
                        throw Unreadable(new InvalidDataException(
                            "the archive's list of files and its contents disagree"));
                    }

                    if (!wanted.Contains(index))
                    {
                        continue;
                    }

                    await using var stream = new ArchiveEntryStream(
                        reader.OpenEntryStream, Entries[index].Name, IsDamage);
                    await read(Entries[index], stream).ConfigureAwait(false);
                }
            }
        }

        public override void Dispose()
        {
            _archive?.Dispose();
            _file.Dispose();
        }

        /// <summary>Moves to the next file; passing over an unread one unpacks it, and can fail as reading can.</summary>
        private static bool Next(IReader reader)
        {
            try
            {
                return reader.MoveToNextEntry();
            }
            catch (Exception ex) when (IsDamage(ex))
            {
                throw Unreadable(ex);
            }
        }

        private static FileRefusedException Unreadable(Exception inner) => new(
            "The archive could not be unpacked: it is damaged, or was not transferred whole. Nothing was kept.",
            inner);
    }
}

/// <summary>
/// One file being unpacked: what goes wrong while reading it is the archive's fault, and is said so.
/// </summary>
/// <remarks>
/// <para>
/// The stream is handed to the file store, which reads it while writing and hashing. A fault in
/// the archive therefore surfaces in the middle of the store's work, beside faults that are not
/// the archive's at all - a full disk, a lost database. Turned into a refusal here, where it
/// happens, the two can never be confused: what leaves this class as
/// <see cref="FileRefusedException"/> is the archive's, and anything else is not.
/// </para>
/// <para>
/// <b>The ZIP check.</b> Given the CRC-32 the archive declares, this stream sums what passes and
/// refuses at the last byte if it does not match - which is before the store moves the file into
/// place, so a file that fails it is never stored. The size is not checked beside it: the runtime
/// stops at the size declared, and a file that came out short or changed cannot match its sum.
/// </para>
/// </remarks>
internal sealed class ArchiveEntryStream : Stream
{
    private readonly string _name;
    private readonly Func<Exception, bool> _isDamage;
    private readonly uint? _expectedCrc32;
    private readonly Func<Stream> _open;
    private Stream? _inner;
    private long _read;
    private uint _crc = 0xFFFFFFFF;
    private bool _ended;

    /// <param name="open">Opens the unpacked stream. Called at the first read, not before.</param>
    /// <param name="name">The file's name, for a refusal.</param>
    /// <param name="isDamage">Which exceptions from reading mean the packed data is bad.</param>
    /// <param name="expectedCrc32">The CRC-32 the archive declares, to be checked; null when the reader checks it.</param>
    public ArchiveEntryStream(
        Func<Stream> open, string name, Func<Exception, bool> isDamage, uint? expectedCrc32 = null)
    {
        _open = open;
        _name = name;
        _isDamage = isDamage;
        _expectedCrc32 = expectedCrc32;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _read;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_ended || buffer.Length == 0)
        {
            return 0;
        }

        int got;

        try
        {
            got = Inner().Read(buffer);
        }
        catch (OutOfMemoryException ex)
        {
            throw TooMuchMemory(ex);
        }
        catch (Exception ex) when (_isDamage(ex))
        {
            throw Damaged(ex);
        }

        return Account(buffer[..got]);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_ended || buffer.Length == 0)
        {
            return 0;
        }

        int got;

        try
        {
            got = await Inner().ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (OutOfMemoryException ex)
        {
            throw TooMuchMemory(ex);
        }
        catch (Exception ex) when (_isDamage(ex))
        {
            throw Damaged(ex);
        }

        return Account(buffer.Span[..got]);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner?.Dispose();
        }

        base.Dispose(disposing);
    }

    // Opened at the first read, not before: a RAR's dictionary is allocated on opening, and that
    // is the allocation that can be too large - it has to happen inside the guarded read.
    private Stream Inner() => _inner ??= _open();

    /// <summary>Sums what was read; at the end, holds it against what the archive declared.</summary>
    private int Account(ReadOnlySpan<byte> data)
    {
        if (data.Length > 0)
        {
            _read += data.Length;

            if (_expectedCrc32 is not null)
            {
                _crc = Crc32.Append(_crc, data);
            }

            return data.Length;
        }

        _ended = true;

        if (_expectedCrc32 is { } expected && (_crc ^ 0xFFFFFFFF) != expected)
        {
            throw Damaged(new InvalidDataException("the unpacked data does not match the archive's checksum"));
        }

        return 0;
    }

    private FileRefusedException Damaged(Exception inner) => new(
        $"{_name} could not be unpacked: the archive is damaged, or was not transferred whole. Nothing was kept.",
        inner);

    private FileRefusedException TooMuchMemory(Exception inner) => new(
        $"{_name} needs more memory to unpack than this server has for it: the archive was made with a very "
        + "large dictionary. Pack it again with the default settings, or send the CSV itself. Nothing was kept.",
        inner);
}

/// <summary>CRC-32 as ZIP and gzip use it (IEEE 802.3, reflected, polynomial 0xEDB88320).</summary>
/// <remarks>
/// Eight tables, eight bytes a step, because a daily file is half a gigabyte and the one-table form
/// would take longer to sum it than the unpacking took. The runtime keeps this in a package of its
/// own (System.IO.Hashing); forty lines here are not worth a dependency.
/// </remarks>
internal static class Crc32
{
    private static readonly uint[][] Tables = BuildTables();

    /// <summary>Folds more bytes into a running value. Start from 0xFFFFFFFF; finish by XOR with it.</summary>
    public static uint Append(uint crc, ReadOnlySpan<byte> data)
    {
        var t = Tables;
        var i = 0;

        for (; i + 8 <= data.Length; i += 8)
        {
            var low = crc ^ System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(i, 4));
            var high = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(i + 4, 4));

            crc = t[7][low & 0xFF] ^ t[6][(low >> 8) & 0xFF] ^ t[5][(low >> 16) & 0xFF] ^ t[4][low >> 24]
                ^ t[3][high & 0xFF] ^ t[2][(high >> 8) & 0xFF] ^ t[1][(high >> 16) & 0xFF] ^ t[0][high >> 24];
        }

        for (; i < data.Length; i++)
        {
            crc = t[0][(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    /// <summary>The CRC-32 of a whole buffer.</summary>
    public static uint Of(ReadOnlySpan<byte> data) => Append(0xFFFFFFFF, data) ^ 0xFFFFFFFF;

    private static uint[][] BuildTables()
    {
        var tables = new uint[8][];
        tables[0] = new uint[256];

        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var bit = 0; bit < 8; bit++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            tables[0][n] = c;
        }

        for (var table = 1; table < 8; table++)
        {
            tables[table] = new uint[256];
            for (var n = 0; n < 256; n++)
            {
                var previous = tables[table - 1][n];
                tables[table][n] = tables[0][previous & 0xFF] ^ (previous >> 8);
            }
        }

        return tables;
    }
}
