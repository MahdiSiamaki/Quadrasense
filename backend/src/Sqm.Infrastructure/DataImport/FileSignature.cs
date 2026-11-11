namespace Sqm.Infrastructure.DataImport;

/// <summary>What a file is, told from its first bytes.</summary>
public enum FileKind
{
    /// <summary>Not a packing this platform knows: taken as the data itself.</summary>
    Plain,

    /// <summary>One file, gzip-compressed.</summary>
    Gzip,

    /// <summary>A ZIP archive.</summary>
    Zip,

    /// <summary>A RAR archive, version 4 or 5.</summary>
    Rar,

    /// <summary>A packing or a document this platform does not unpack; see the name beside it.</summary>
    Unsupported,
}

/// <summary>
/// Tells what a file is from its first bytes, never from its name.
/// </summary>
/// <remarks>
/// <para>
/// A name is what the sender typed. On 2026-11-10 a <c>.rar</c> was sent where a CSV was expected
/// and was read as one: its first "line" was compared with the column contract. The first bytes of
/// a file are what it is, whatever it is called - a ZIP renamed <c>.csv</c> is still a ZIP, and a
/// CSV renamed <c>.zip</c> is still a CSV.
/// </para>
/// <para>
/// Only packings are recognised, because only they change what has to be done. Anything else is
/// taken as the data, and the column contract says whether it is.
/// </para>
/// </remarks>
public static class FileSignature
{
    /// <summary>How many leading bytes are needed to tell: a tar's mark sits at byte 257.</summary>
    public const int HeadBytes = 512;

    /// <summary>What the bytes are.</summary>
    /// <param name="head">The first bytes of the file, up to <see cref="HeadBytes"/> of them.</param>
    /// <param name="what">For <see cref="FileKind.Unsupported"/>: what it is, in words for a person.</param>
    public static FileKind Read(ReadOnlySpan<byte> head, out string? what)
    {
        what = null;

        if (head.StartsWith("PK\u0003\u0004"u8) || head.StartsWith("PK\u0005\u0006"u8) || head.StartsWith("PK\u0007\u0008"u8))
        {
            return FileKind.Zip;
        }

        // "Rar!" 1A 07, then 00 (version 4) or 01 00 (version 5).
        if (head.StartsWith("Rar!\u001a\u0007"u8))
        {
            return FileKind.Rar;
        }

        if (head.StartsWith<byte>([0x1F, 0x8B]))
        {
            return FileKind.Gzip;
        }

        if (head.StartsWith<byte>([0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C]))
        {
            what = "a 7z archive";
        }
        else if (head.StartsWith<byte>([0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00]))
        {
            what = "an xz file";
        }
        else if (head.StartsWith<byte>([0x28, 0xB5, 0x2F, 0xFD]))
        {
            what = "a Zstandard file";
        }
        else if (head.Length > 3 && head.StartsWith("BZh"u8) && head[3] is >= (byte)'1' and <= (byte)'9')
        {
            // "BZh" and a block size of 1 to 9: a CSV could begin with the letters, hardly with the digit too.
            what = "a bzip2 file";
        }
        else if (head.Length >= 262 && head.Slice(257, 5).SequenceEqual("ustar"u8))
        {
            what = "a tar archive";
        }
        else if (head.StartsWith<byte>([0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1]))
        {
            what = "an older Office document (.xls or .doc)";
        }

        return what is null ? FileKind.Plain : FileKind.Unsupported;
    }

    /// <summary>The formats that are taken, for a refusal to name.</summary>
    public const string Accepted = "CSV, or a CSV packed as ZIP, RAR or GZ";
}
