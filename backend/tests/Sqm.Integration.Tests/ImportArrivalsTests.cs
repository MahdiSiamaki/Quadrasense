using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sqm.Application.DataImport;
using Sqm.Infrastructure.DataImport;

namespace Sqm.Integration.Tests;

/// <summary>
/// Whatever arrives - a CSV, or one packed as GZ, ZIP or RAR - ends as the same stored file, or as
/// nothing at all.
/// </summary>
/// <remarks>
/// <para>
/// The real file store in a temporary folder, and the readers that ship reading real archives: ZIP
/// and gzip made here, RAR from <c>Fixtures/Archives</c>, which were packed once with WinRAR from
/// synthetic files (nothing free writes RAR). The repository is a stand-in that remembers what it
/// was told, so that "the same content twice" can be asked of it.
/// </para>
/// <para>
/// What these hold the code to: what is stored is the file inside and is byte-for-byte what was
/// packed; a refusal leaves nothing behind, in the store or beside it; and an archive is taken
/// whole or not at all.
/// </para>
/// </remarks>
public sealed class ImportArrivalsTests : IDisposable
{
    // The files the RAR fixtures were packed from, by the formulas below (make_fixtures.py).
    private const string CatalogueSha = "14144a3060817b98b8a9778ca1866ae3b007002e0b23681305c1139e114e0cb7";
    private const string Day17Sha = "bb666e8d6c96c216649b4945c2dd31f60b01ac45a96c0f77aa5b06d69a6ecf23";
    private const string Day18Sha = "a8ab17e4db2dace05bbb7fa5ecd7dc8090d7d0656099f33a925ceba8502f31f6";

    private static readonly DateOnly Today = new(2026, 11, 11);

    private static readonly byte[] Catalogue = Encoding.UTF8.GetBytes(
        "tac,manufacturer,brandName,modelName\n" + string.Concat(Enumerable.Range(1, 60).Select(i =>
            string.Create(CultureInfo.InvariantCulture, $"350{i:00000},Test Maker {i % 7},Test,Model {i}\n"))));

    private readonly string _root = Path.Combine(Path.GetTempPath(), "sqm-arrivals-" + Guid.NewGuid().ToString("N"));
    private readonly ImportLedger _ledger = new();

    private static byte[] Day(int first, int rows) => Encoding.UTF8.GetBytes(
        "msisdn,imsi,imei,action\n" + string.Concat(Enumerable.Range(first, rows).Select(i => string.Create(
            CultureInfo.InvariantCulture,
            $"91200{i:00000},4321100000{i:00000},350040120{i:00000},{(i % 3 != 0 ? "add" : "remove")}\n"))));

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    // ------------------------------------------------------------------ RAR, as GSMA delivers it

    [Fact]
    public void The_fixtures_hold_what_the_formulas_here_produce()
    {
        // Ties the committed archives to the expectations below: were either changed alone, this says so.
        Assert.Equal(CatalogueSha, Sha(Catalogue));
        Assert.Equal(Day17Sha, Sha(Day(1, 50)));
        Assert.Equal(Day18Sha, Sha(Day(51, 40)));
    }

    [Fact]
    public async Task A_rar_is_unpacked_and_the_file_inside_is_what_is_stored_byte_for_byte()
    {
        var report = await TakeAsync("TAC", "DeviceDatabaseTAC09Nov2026.rar", Fixture("DeviceDatabaseTAC09Nov2026.rar"));

        Assert.Equal(FileKind.Rar, report.Kind);
        Assert.True(report.IsArchive);
        Assert.Empty(report.Skipped);

        var file = Assert.Single(report.Files);
        Assert.Equal("DeviceDatabase.csv", file.FileName);
        Assert.Equal(CatalogueSha, file.Stored.Sha256);
        Assert.Equal(Catalogue.Length, file.Stored.SizeBytes);
        Assert.Equal(IntakeOutcome.Queued, file.Intake.Outcome);

        // In the store: the CSV, whole - and neither the archive nor a copy of it anywhere.
        var stored = Assert.Single(StoredFiles());
        Assert.EndsWith(".csv", stored, StringComparison.Ordinal);
        Assert.Equal(Catalogue, await File.ReadAllBytesAsync(stored, TestContext.Current.CancellationToken));
        Assert.Empty(Litter());
    }

    [Fact]
    public async Task The_only_file_of_an_archive_is_dated_by_the_archives_name()
    {
        // The date is on GSMA's wrapping; the file inside is DeviceDatabase.csv.
        await TakeAsync("TAC", "DeviceDatabaseTAC09Nov2026.rar", Fixture("DeviceDatabaseTAC09Nov2026.rar"));

        var row = Assert.Single(_ledger.Files);
        Assert.Equal("DeviceDatabase.csv", row.Name);
        Assert.Equal("DeviceDatabaseTAC09Nov2026.rar", row.Archive);
        Assert.Equal(new DateOnly(2026, 11, 9), row.Date);
        Assert.Equal(FileDateSource.Archive, row.DateSource);

        // A snapshot's job carries no business date; a person can read where the date came from.
        Assert.Equal([("TAC", (DateOnly?)null)], _ledger.Jobs.Select(j => (j.Source, j.BusinessDate)));
        Assert.Equal(
            ["Dated 2026-11-09, from the name of the archive it arrived in, DeviceDatabaseTAC09Nov2026.rar."],
            _ledger.Events);
    }

    [Fact]
    public async Task The_same_file_sent_bare_and_sent_packed_is_one_file_imported_once()
    {
        var bare = await TakeAsync("TAC", "DeviceDatabaseTAC09Nov2026.csv", Catalogue);
        var packed = await TakeAsync("TAC", "DeviceDatabaseTAC09Nov2026.rar", Fixture("DeviceDatabaseTAC09Nov2026.rar"));
        var zipped = await TakeAsync("TAC", "anything.zip", Zip(("DeviceDatabase.csv", Catalogue)));
        var gzipped = await TakeAsync("TAC", "DeviceDatabaseTAC09Nov2026.csv.gz", Gzip(Catalogue));

        Assert.Equal(IntakeOutcome.Queued, Assert.Single(bare.Files).Intake.Outcome);
        Assert.Equal(IntakeOutcome.AlreadyImported, Assert.Single(packed.Files).Intake.Outcome);
        Assert.Equal(IntakeOutcome.AlreadyImported, Assert.Single(zipped.Files).Intake.Outcome);
        Assert.Equal(IntakeOutcome.AlreadyImported, Assert.Single(gzipped.Files).Intake.Outcome);

        // One content hash, one stored file, one job.
        Assert.Single(StoredFiles());
        Assert.Single(_ledger.Files);
        Assert.Single(_ledger.Jobs);
        Assert.Empty(Litter());
    }

    [Fact]
    public async Task A_solid_rar_of_two_days_gives_two_files_each_under_its_own_day()
    {
        var report = await TakeAsync("SQM", "october.rar", Fixture("two-days-solid.rar"));

        Assert.Equal(
            [("daily_subs_device_sim_info_2026-10-17.csv", Day17Sha), ("daily_subs_device_sim_info_2026-10-18.csv", Day18Sha)],
            report.Files.Select(f => (f.FileName, f.Stored.Sha256)));
        Assert.All(report.Files, f => Assert.Equal(IntakeOutcome.Queued, f.Intake.Outcome));
        Assert.Equal(
            [("SQM", (DateOnly?)new DateOnly(2026, 10, 17)), ("SQM", new DateOnly(2026, 10, 18))],
            _ledger.Jobs.Select(j => (j.Source, j.BusinessDate)));
        Assert.All(_ledger.Files, f => Assert.Equal(FileDateSource.Name, f.DateSource));
        Assert.Equal(2, StoredFiles().Count);
    }

    [Fact]
    public async Task Only_csv_files_are_taken_from_an_archive_and_each_thing_left_says_why()
    {
        var report = await TakeAsync("TAC", "mixed.rar", Fixture("mixed.rar"));

        // A folder inside the archive is not part of the file's name.
        var file = Assert.Single(report.Files);
        Assert.Equal("DeviceDatabase.csv", file.FileName);
        Assert.Equal(CatalogueSha, file.Stored.Sha256);

        Assert.Equal(
            [
                ("__MACOSX/._DeviceDatabase.csv", "Not data: the packing program adds it."),
                ("empty.csv", "It is empty."),
                ("packed.csv", "It is itself an archive: a packing inside a packing is not unpacked."),
                ("readme.txt", "Not a .csv file."),
            ],
            report.Skipped.Select(s => (s.Name.Replace('\\', '/'), s.Reason)).Order());

        // Three files in it were named .csv, so its name would have dated none of them; and its
        // name has no date anyway. The one file taken waits for a person.
        Assert.Equal(IntakeOutcome.WaitingForDate, file.Intake.Outcome);
        Assert.Equal(FileDateState.Missing, file.Intake.Date.State);
        Assert.Empty(_ledger.Jobs);
        Assert.Single(StoredFiles());
    }

    [Theory]
    [InlineData("password.rar")]
    [InlineData("password-names.rar")]
    public async Task An_archive_with_a_password_is_refused(string fixture)
    {
        var refusal = await Assert.ThrowsAsync<FileRefusedException>(
            () => TakeAsync("TAC", fixture, Fixture(fixture)));

        Assert.Equal("The archive is protected by a password. Send it without one. Nothing was kept.", refusal.Message);
        AssertNothingKept();
    }

    [Fact]
    public async Task One_volume_of_a_split_archive_is_refused()
    {
        var refusal = await Assert.ThrowsAsync<FileRefusedException>(
            () => TakeAsync("SQM", "split.part1.rar", Fixture("split.part1.rar")));

        Assert.StartsWith("split.part1.rar is one part of an archive split into several.", refusal.Message, StringComparison.Ordinal);
        AssertNothingKept();
    }

    [Fact]
    public async Task A_rar_with_one_byte_changed_is_refused_and_nothing_of_it_is_kept()
    {
        var damaged = Fixture("DeviceDatabaseTAC09Nov2026.rar");
        damaged[damaged.Length * 3 / 4] ^= 0x01;

        var refusal = await Assert.ThrowsAsync<FileRefusedException>(
            () => TakeAsync("TAC", "DeviceDatabaseTAC09Nov2026.rar", damaged));

        Assert.Contains("damaged, or was not transferred whole", refusal.Message, StringComparison.Ordinal);
        AssertNothingKept();
    }

    // ------------------------------------------------------------------ ZIP

    [Fact]
    public async Task A_zip_is_unpacked_the_same_way_whatever_it_is_called()
    {
        // Told from its first bytes: this one calls itself a CSV.
        var report = await TakeAsync(
            "SQM", "daily_subs_device_sim_info_2026-10-17.csv",
            Zip(("daily_subs_device_sim_info_2026-10-17.csv", Day(1, 50))));

        Assert.Equal(FileKind.Zip, report.Kind);
        var file = Assert.Single(report.Files);
        Assert.Equal(Day17Sha, file.Stored.Sha256);
        Assert.Equal(IntakeOutcome.Queued, file.Intake.Outcome);
        Assert.Equal(new DateOnly(2026, 10, 17), Assert.Single(_ledger.Jobs).BusinessDate);
        Assert.Empty(Litter());
    }

    [Fact]
    public async Task A_csv_that_calls_itself_a_zip_is_still_a_csv()
    {
        var report = await TakeAsync("SQM", "subs_2026-10-17.zip", Day(1, 50));

        Assert.Equal(FileKind.Plain, report.Kind);
        Assert.Equal(Day17Sha, Assert.Single(report.Files).Stored.Sha256);
    }

    [Theory]
    [InlineData(CompressionLevel.NoCompression)]
    [InlineData(CompressionLevel.Optimal)]
    public async Task A_zip_with_one_byte_changed_is_refused_though_the_runtime_reads_it_without_complaint(
        CompressionLevel level)
    {
        var zip = Zip(level, ("daily_subs_device_sim_info_2026-10-17.csv", Day(1, 50)));

        // Inside the file's data: for a stored entry the text itself, for a deflated one its middle.
        var at = level == CompressionLevel.NoCompression
            ? zip.AsSpan().IndexOf("9120000020"u8) + 4
            : 30 + "daily_subs_device_sim_info_2026-10-17.csv".Length + 200;
        zip[at] ^= 0x01;

        // The runtime's reader alone hands the changed bytes over as if nothing were wrong: this
        // is what the check in ArchiveEntryStream exists for.
        using (var plain = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read))
        using (var read = new MemoryStream())
        {
            await plain.Entries[0].Open().CopyToAsync(read, TestContext.Current.CancellationToken);
            Assert.NotEqual(Day17Sha, Sha(read.ToArray()));
        }

        var refusal = await Assert.ThrowsAsync<FileRefusedException>(
            () => TakeAsync("SQM", "october.zip", zip));

        Assert.Equal(
            "daily_subs_device_sim_info_2026-10-17.csv could not be unpacked: the archive is damaged, or was not "
            + "transferred whole. Nothing was kept.",
            refusal.Message);
        AssertNothingKept();
    }

    [Fact]
    public async Task An_archive_is_taken_whole_or_not_at_all()
    {
        // The first file is sound and is unpacked and stored before the second is found damaged.
        var zip = Zip(
            CompressionLevel.NoCompression,
            ("daily_subs_device_sim_info_2026-10-17.csv", Day(1, 50)),
            ("daily_subs_device_sim_info_2026-10-18.csv", Day(51, 40)));
        zip[zip.AsSpan().IndexOf("9120000070"u8) + 4] ^= 0x01;

        var refusal = await Assert.ThrowsAsync<FileRefusedException>(() => TakeAsync("SQM", "october.zip", zip));

        Assert.StartsWith("daily_subs_device_sim_info_2026-10-18.csv could not be unpacked", refusal.Message, StringComparison.Ordinal);
        AssertNothingKept();
    }

    [Fact]
    public async Task A_zip_with_a_password_is_refused()
    {
        var zip = Zip(("DeviceDatabase.csv", Catalogue));

        // The "encrypted" bit of the entry's flags, where the list of files at the end records it.
        var central = zip.AsSpan().LastIndexOf("PK\u0001\u0002"u8);
        zip[central + 8] |= 0x01;

        var refusal = await Assert.ThrowsAsync<FileRefusedException>(() => TakeAsync("TAC", "tac.zip", zip));

        Assert.Equal("The archive is protected by a password. Send it without one. Nothing was kept.", refusal.Message);
        AssertNothingKept();
    }

    [Fact]
    public async Task A_zip_cut_short_is_refused()
    {
        var zip = Zip(("DeviceDatabase.csv", Catalogue));

        var refusal = await Assert.ThrowsAsync<FileRefusedException>(
            () => TakeAsync("TAC", "tac.zip", zip[..(zip.Length - 40)]));

        Assert.Equal(
            "tac.zip could not be opened: it is damaged, or was not transferred whole. Nothing was kept.",
            refusal.Message);
        AssertNothingKept();
    }

    [Fact]
    public async Task An_archive_with_nothing_to_import_is_refused_and_says_what_it_held()
    {
        var refusal = await Assert.ThrowsAsync<FileRefusedException>(() => TakeAsync(
            "SQM", "notes.zip", Zip(("readme.txt", "x"u8.ToArray()), ("report/figures.xlsx", "PK"u8.ToArray()))));

        Assert.Equal(
            "notes.zip holds no CSV file to import. It holds readme.txt, report/figures.xlsx. Nothing was kept.",
            refusal.Message);
        AssertNothingKept();
    }

    [Fact]
    public async Task An_archive_of_several_files_is_not_dated_by_its_own_name()
    {
        // The archive's name is about the set: which of the two is "the 17th" it does not say.
        var report = await TakeAsync(
            "SQM", "week_2026-10-17.zip", Zip(("first.csv", Day(1, 50)), ("second.csv", Day(51, 40))));

        Assert.All(report.Files, f => Assert.Equal(IntakeOutcome.WaitingForDate, f.Intake.Outcome));
        Assert.All(report.Files, f => Assert.Equal(FileDateState.Missing, f.Intake.Date.State));
        Assert.Empty(_ledger.Jobs);
        Assert.Equal(2, StoredFiles().Count);
    }

    [Fact]
    public void What_a_packing_program_adds_is_not_data_and_only_csv_is()
    {
        ArchiveEntry[] entries =
        [
            new("DeviceDatabase.csv", 10), new("days/DAY.CSV", 10), new("readme.txt", 10), new("sheet.xlsx", 10),
            new("__MACOSX/DeviceDatabase.csv", 10), new("__MACOSX/days/._DAY.CSV", 10), new(".hidden.csv", 10),
            new(".DS_Store", 10), new("days/Thumbs.db", 10), new("desktop.ini", 10), new("csv", 10),
        ];

        var (wanted, skipped) = ArchiveContents.Choose(entries);

        Assert.Equal(["DeviceDatabase.csv", "days/DAY.CSV"], wanted.Select(i => entries[i].Path));
        Assert.Equal(
            ["__MACOSX/DeviceDatabase.csv", "__MACOSX/days/._DAY.CSV", ".hidden.csv", ".DS_Store", "days/Thumbs.db", "desktop.ini"],
            skipped.Where(s => s.Reason == "Not data: the packing program adds it.").Select(s => s.Name));
        Assert.Equal(
            ["readme.txt", "sheet.xlsx", "csv"],
            skipped.Where(s => s.Reason == "Not a .csv file.").Select(s => s.Name));
    }

    [Fact]
    public async Task A_file_inside_an_archive_is_stored_under_its_own_name_and_never_its_path()
    {
        // An archive is somebody else's program for what to write where.
        var report = await TakeAsync(
            "SQM", "escape.zip", Zip(("../../../outside/daily_subs_device_sim_info_2026-10-17.csv", Day(1, 50))));

        Assert.Equal("daily_subs_device_sim_info_2026-10-17.csv", Assert.Single(report.Files).FileName);

        var stored = Assert.Single(StoredFiles());
        Assert.StartsWith(Path.Combine(_root, "SQM"), stored, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(_root)!, "outside")));
    }

    // ------------------------------------------------------------------ what an archive may cost

    [Fact]
    public async Task More_files_than_are_taken_from_one_archive_refuse_it_before_any_is_unpacked()
    {
        var zip = Zip(("a.csv", Day(1, 3)), ("b.csv", Day(4, 3)), ("c.csv", Day(7, 3)));

        var refusal = await Assert.ThrowsAsync<FileRefusedException>(
            () => TakeAsync("SQM", "many.zip", zip, o => o.MaxArchiveFiles = 2));

        Assert.StartsWith("many.zip holds 3 CSV files; at most 2 are taken from one archive.", refusal.Message, StringComparison.Ordinal);
        AssertNothingKept();
    }

    [Fact]
    public async Task A_small_archive_that_unpacks_to_a_great_deal_is_refused_by_what_it_declares()
    {
        // Five megabytes of one letter pack to a few kilobytes: the shape of a zip bomb.
        var zip = Zip(CompressionLevel.Optimal, ("big.csv", Enumerable.Repeat((byte)'a', 5 << 20).ToArray()));
        Assert.True(zip.Length < 16_384);

        var perFile = await Assert.ThrowsAsync<FileRefusedException>(
            () => TakeAsync("SQM", "big.zip", zip, o => o.MaxFileBytes = 1 << 20));
        Assert.Equal("big.csv in big.zip unpacks to 5 MB; the largest file taken is 1 MB. Nothing was kept.", perFile.Message);

        var inAll = await Assert.ThrowsAsync<FileRefusedException>(
            () => TakeAsync("SQM", "big.zip", zip, o => o.MaxArchiveUnpackedBytes = 2 << 20));
        Assert.StartsWith("big.zip unpacks to 5 MB; at most 2 MB is unpacked from one archive.", inAll.Message, StringComparison.Ordinal);

        AssertNothingKept();
    }

    [Fact]
    public async Task An_archive_is_refused_when_unpacking_it_would_not_leave_the_volume_room()
    {
        var zip = Zip(("daily_subs_device_sim_info_2026-10-17.csv", Day(1, 50)));

        var refusal = await Assert.ThrowsAsync<FileRefusedException>(
            () => TakeAsync("SQM", "october.zip", zip, o => o.MinFreeBytesAfterUnpack = long.MaxValue / 2));

        Assert.Contains("must stay free after it", refusal.Message, StringComparison.Ordinal);
        AssertNothingKept();
    }

    [Fact]
    public void The_room_on_a_volume_can_be_read_here()
    {
        // The check above is skipped where this answers null: it must not answer null on the
        // systems this runs on, Windows and the Linux of the containers.
        Directory.CreateDirectory(_root);

        Assert.True(FreeSpace.TryGet(_root) > 0);
    }

    // ------------------------------------------------------------------ gzip

    [Fact]
    public async Task A_gzip_is_stored_as_what_it_holds_under_its_name_without_gz()
    {
        var report = await TakeAsync("SQM", "daily_subs_device_sim_info_2026-10-17.csv.gz", Gzip(Day(1, 50)));

        Assert.Equal(FileKind.Gzip, report.Kind);
        Assert.False(report.IsArchive);

        var file = Assert.Single(report.Files);
        Assert.Equal("daily_subs_device_sim_info_2026-10-17.csv", file.FileName);
        Assert.Equal(Day17Sha, file.Stored.Sha256);
        Assert.Equal(new DateOnly(2026, 10, 17), Assert.Single(_ledger.Jobs).BusinessDate);
        Assert.Null(Assert.Single(_ledger.Files).Archive);
    }

    [Fact]
    public async Task A_gzip_cut_short_in_transfer_is_refused_and_not_imported_as_a_short_day()
    {
        // Enough rows that the cut falls well inside the data.
        var whole = Gzip(Day(1, 20_000));
        var cut = whole[..(whole.Length / 2)];

        var refusal = await Assert.ThrowsAsync<FileRefusedException>(
            () => TakeAsync("SQM", "daily_subs_device_sim_info_2026-10-17.csv.gz", cut));

        Assert.Contains("damaged, or was not transferred whole", refusal.Message, StringComparison.Ordinal);
        AssertNothingKept();
    }

    [Fact]
    public async Task A_gzip_with_one_byte_changed_is_refused()
    {
        var gzip = Gzip(Day(1, 20_000));
        gzip[gzip.Length / 2] ^= 0x01;

        var refusal = await Assert.ThrowsAsync<FileRefusedException>(
            () => TakeAsync("SQM", "daily_subs_device_sim_info_2026-10-17.csv.gz", gzip));

        Assert.Contains("damaged, or was not transferred whole", refusal.Message, StringComparison.Ordinal);
        AssertNothingKept();
    }

    [Fact]
    public async Task A_gzip_that_holds_another_packing_is_refused()
    {
        // The start of a tar: a 512-byte header with its mark at byte 257.
        var tar = new byte[1024];
        "daily.csv"u8.CopyTo(tar);
        "ustar"u8.CopyTo(tar.AsSpan(257));

        var tarred = await Assert.ThrowsAsync<FileRefusedException>(() => TakeAsync("SQM", "days.tar.gz", Gzip(tar)));
        Assert.StartsWith("days.tar.gz holds a tar archive, not a CSV", tarred.Message, StringComparison.Ordinal);

        var zipped = await Assert.ThrowsAsync<FileRefusedException>(
            () => TakeAsync("SQM", "days.zip.gz", Gzip(Zip(("a.csv", Day(1, 3))))));
        Assert.StartsWith("days.zip.gz holds another archive, not a CSV", zipped.Message, StringComparison.Ordinal);

        AssertNothingKept();
    }

    [Fact]
    public async Task A_gzip_that_unpacks_past_the_largest_file_taken_is_stopped_where_it_passes_it()
    {
        // A gzip says how large it is only in its last bytes: this is caught by counting what comes out.
        var gzip = Gzip(Enumerable.Repeat((byte)'a', 5 << 20).ToArray());

        var refusal = await Assert.ThrowsAsync<FileRefusedException>(
            () => TakeAsync("SQM", "subs_2026-10-17.csv.gz", gzip, o => o.MaxFileBytes = 1 << 20));

        Assert.Equal(
            "subs_2026-10-17.csv unpacks to more than 1 MB, which is more than is taken. Nothing was kept.",
            refusal.Message);
        AssertNothingKept();
    }

    // ------------------------------------------------------------------ a plain file, and what is not one

    [Fact]
    public async Task A_plain_csv_is_stored_as_it_arrives()
    {
        var report = await TakeAsync("SQM", "daily_subs_device_sim_info_2026-10-17.csv", Day(1, 50));

        Assert.Equal(FileKind.Plain, report.Kind);
        Assert.Equal(Day17Sha, Assert.Single(report.Files).Stored.Sha256);
        Assert.Equal(Day(1, 50), await File.ReadAllBytesAsync(Assert.Single(StoredFiles()), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0x00, 0x04 }, "a 7z archive")]
    [InlineData(new byte[] { 0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00, 0x00, 0x04 }, "an xz file")]
    [InlineData(new byte[] { 0x42, 0x5A, 0x68, 0x39, 0x31, 0x41, 0x59, 0x26 }, "a bzip2 file")]
    [InlineData(new byte[] { 0x28, 0xB5, 0x2F, 0xFD, 0x24, 0x00, 0x01, 0x00 }, "a Zstandard file")]
    [InlineData(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }, "an older Office document (.xls or .doc)")]
    public async Task A_packing_that_is_not_unpacked_here_is_refused_by_name(byte[] start, string what)
    {
        var refusal = await Assert.ThrowsAsync<FileRefusedException>(
            () => TakeAsync("SQM", "days.dat", [.. start, .. new byte[64]]));

        Assert.Equal(
            $"days.dat is {what}, which is not unpacked here. Send CSV, or a CSV packed as ZIP, RAR or GZ. Nothing was kept.",
            refusal.Message);
        AssertNothingKept();
    }

    [Fact]
    public async Task An_empty_file_is_refused()
    {
        var refusal = await Assert.ThrowsAsync<FileRefusedException>(() => TakeAsync("SQM", "nothing.csv", []));

        Assert.Equal("nothing.csv is empty.", refusal.Message);
        AssertNothingKept();
    }

    [Fact]
    public async Task A_file_larger_than_any_taken_is_refused_with_a_reason_and_not_a_fault()
    {
        var refusal = await Assert.ThrowsAsync<FileRefusedException>(
            () => TakeAsync("SQM", "daily_subs_device_sim_info_2026-10-17.csv", Day(1, 50), o => o.MaxFileBytes = 100));

        Assert.Equal(
            "daily_subs_device_sim_info_2026-10-17.csv is larger than 100 bytes, the largest file taken. Nothing was kept.",
            refusal.Message);
        AssertNothingKept();
    }

    // ------------------------------------------------------------------ a file already on disk

    [Fact]
    public async Task A_finished_upload_that_is_a_csv_is_moved_into_the_store_and_one_that_is_packed_is_unpacked()
    {
        Directory.CreateDirectory(Path.Combine(_root, "_resumable"));
        var ct = TestContext.Current.CancellationToken;

        var plain = Path.Combine(_root, "_resumable", "0123456789abcdef0123456789abcdef");
        await File.WriteAllBytesAsync(plain, Day(1, 50), ct);
        var moved = await Arrivals().TakeFileAsync(
            "SQM", "daily_subs_device_sim_info_2026-10-17.csv", plain, adopt: true, "tester", null, null, ct);

        Assert.Equal(Day17Sha, Assert.Single(moved.Files).Stored.Sha256);
        Assert.False(File.Exists(plain));

        var packed = Path.Combine(_root, "_resumable", "fedcba9876543210fedcba9876543210");
        await File.WriteAllBytesAsync(packed, Fixture("two-days-solid.rar"), ct);
        var unpacked = await Arrivals().TakeFileAsync("SQM", "october.rar", packed, adopt: true, "tester", null, null, ct);

        // The first day is the file just moved in: the same content, so it is already imported.
        Assert.Equal(
            [IntakeOutcome.AlreadyImported, IntakeOutcome.Queued], unpacked.Files.Select(f => f.Intake.Outcome));
        Assert.Equal(2, StoredFiles().Count);

        // The archive is the caller's to remove - it is the upload's own file - and is still there.
        Assert.True(File.Exists(packed));
    }

    [Fact]
    public async Task A_file_handed_over_from_this_machine_is_copied_and_left_where_it_was()
    {
        var path = Path.Combine(Path.GetTempPath(), "sqm-arrivals-source-" + Guid.NewGuid().ToString("N") + ".csv.gz");
        var ct = TestContext.Current.CancellationToken;
        await File.WriteAllBytesAsync(path, Gzip(Day(1, 50)), ct);

        try
        {
            var report = await Arrivals().TakeFileAsync(
                "SQM", "daily_subs_device_sim_info_2026-10-17.csv.gz", path, adopt: false, "tester", null, null, ct);

            Assert.Equal(Day17Sha, Assert.Single(report.Files).Stored.Sha256);
            Assert.True(File.Exists(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ------------------------------------------------------------------ dates, through the whole arrival

    [Fact]
    public async Task A_file_that_waited_for_its_date_is_queued_when_the_same_content_arrives_dated()
    {
        // 2026-11-10, as it happened: the catalogue sent as the bare CSV, whose name has no date.
        var bare = await TakeAsync("TAC", "DeviceDatabase.csv", Catalogue);

        Assert.Equal(IntakeOutcome.WaitingForDate, Assert.Single(bare.Files).Intake.Outcome);
        Assert.Empty(_ledger.Jobs);
        Assert.Null(Assert.Single(_ledger.Files).Date);

        // Then as GSMA packs it. The content is known and has no job: it goes on now, dated.
        var packed = await TakeAsync("TAC", "DeviceDatabaseTAC09Nov2026.rar", Fixture("DeviceDatabaseTAC09Nov2026.rar"));

        Assert.Equal(IntakeOutcome.Queued, Assert.Single(packed.Files).Intake.Outcome);
        var row = Assert.Single(_ledger.Files);
        Assert.Equal(new DateOnly(2026, 11, 9), row.Date);
        Assert.Equal(FileDateSource.Archive, row.DateSource);
        Assert.Equal("DeviceDatabaseTAC09Nov2026.rar", row.Archive);
        Assert.Single(_ledger.Jobs);
        Assert.Single(StoredFiles());
    }

    [Fact]
    public async Task A_copy_whose_name_says_nothing_goes_on_under_the_date_its_content_already_has()
    {
        // Registered under a name that said its date, and left with no job - an upload that ended
        // between the two. The same content comes again under a name that says nothing.
        _ledger.Files.Add(new ImportLedger.Row(1, "TAC", "DeviceDatabaseTAC09Nov2026.csv", CatalogueSha, "TAC/earlier.csv", Catalogue.Length)
        {
            Date = new DateOnly(2026, 11, 9),
            DateSource = FileDateSource.Name,
        });

        var report = await TakeAsync("TAC", "DeviceDatabase.csv", Catalogue);

        var intake = Assert.Single(report.Files).Intake;
        Assert.Equal(IntakeOutcome.Queued, intake.Outcome);
        Assert.Equal(new DateOnly(2026, 11, 9), intake.Date.Date);
        Assert.Equal(["Dated 2026-11-09, as recorded when it arrived."], _ledger.Events);

        // What was recorded stays as it was recorded: this copy knew nothing to add to it.
        Assert.Equal(FileDateSource.Name, Assert.Single(_ledger.Files).DateSource);
    }

    [Theory]
    [InlineData("subs_03-04-2026.csv", FileDateState.Ambiguous, "2026-03-04,2026-04-03")]
    [InlineData("subs.csv", FileDateState.Missing, "")]
    [InlineData("subs_2062-10-17.csv", FileDateState.Future, "")]
    public async Task A_daily_file_whose_name_does_not_settle_its_day_is_stored_and_not_queued(
        string name, FileDateState state, string candidates)
    {
        var report = await TakeAsync("SQM", name, Day(1, 50));

        var intake = Assert.Single(report.Files).Intake;
        Assert.Equal(IntakeOutcome.WaitingForDate, intake.Outcome);
        Assert.Equal(state, intake.Date.State);
        Assert.Equal(candidates, string.Join(',', intake.Date.Candidates.Select(FileDate.Text)));

        // Kept, so that nothing has to be sent again; with no job, so that no day is guessed.
        Assert.Single(StoredFiles());
        Assert.Empty(_ledger.Jobs);
        Assert.Equal(["import.upload.undated"], _ledger.Audits);
    }

    [Fact]
    public async Task A_waiting_file_is_queued_with_the_date_a_person_gives_it_and_only_one_its_name_can_mean()
    {
        var ct = TestContext.Current.CancellationToken;
        await TakeAsync("SQM", "subs_03-04-2026.csv", Day(1, 50));
        var fileId = Assert.Single(_ledger.Files).FileId;
        var store = Store();

        var listed = Assert.Single(await ImportIntake.ListWaitingAsync(_ledger.Repository, Today, ct));
        Assert.Equal(FileDateState.Ambiguous, listed.Date.State);

        // No date: it still waits. A date the name cannot mean: refused.
        var asked = await ImportIntake.QueueStoredFileAsync(
            _ledger.Repository, store, "SQM", fileId, null, "admin", null, Today, ct);
        Assert.Equal(ReprocessOutcome.DateNeeded, asked.Outcome);

        var wrong = await ImportIntake.QueueStoredFileAsync(
            _ledger.Repository, store, "SQM", fileId, new DateOnly(2026, 4, 4), "admin", null, Today, ct);
        Assert.Equal(ReprocessOutcome.DateRefused, wrong.Outcome);
        Assert.Equal("The name can mean 2026-03-04 or 2026-04-03, not 2026-04-04.", wrong.Problem);
        Assert.Empty(_ledger.Jobs);

        var queued = await ImportIntake.QueueStoredFileAsync(
            _ledger.Repository, store, "SQM", fileId, new DateOnly(2026, 4, 3), "admin", null, Today, ct);
        Assert.Equal(ReprocessOutcome.Queued, queued.Outcome);

        var row = Assert.Single(_ledger.Files);
        Assert.Equal(new DateOnly(2026, 4, 3), row.Date);
        Assert.Equal(FileDateSource.Person, row.DateSource);
        Assert.Equal(new DateOnly(2026, 4, 3), Assert.Single(_ledger.Jobs).BusinessDate);
        Assert.Equal("Dated 2026-04-03, by admin: its name did not settle it.", _ledger.Events[^1]);
        Assert.Empty(await ImportIntake.ListWaitingAsync(_ledger.Repository, Today, ct));
    }

    // ------------------------------------------------------------------ the small parts

    [Theory]
    [InlineData("", 0x00000000u)]
    [InlineData("a", 0xE8B7BE43u)]
    [InlineData("123456789", 0xCBF43926u)]
    [InlineData("The quick brown fox jumps over the lazy dog", 0x414FA339u)]
    public void The_checksum_is_the_one_zip_and_gzip_use(string text, uint expected)
    {
        Assert.Equal(expected, Crc32.Of(Encoding.ASCII.GetBytes(text)));
    }

    [Fact]
    public void The_checksum_is_the_same_however_the_bytes_are_handed_over()
    {
        // Eight bytes a step, with the ends done singly: every way of cutting must give one answer,
        // and it must be the answer the runtime's own ZIP writer records.
        var data = Day(1, 300);
        var whole = Crc32.Of(data);

        foreach (var cut in new[] { 1, 3, 7, 8, 9, 15, 16, 17, 1000 })
        {
            var running = 0xFFFFFFFF;
            for (var at = 0; at < data.Length; at += cut)
            {
                running = Crc32.Append(running, data.AsSpan(at, Math.Min(cut, data.Length - at)));
            }

            Assert.Equal(whole, running ^ 0xFFFFFFFF);
        }

        using var zip = new ZipArchive(new MemoryStream(Zip(("a.csv", data))), ZipArchiveMode.Read);
        Assert.Equal(zip.Entries[0].Crc32, whole);
    }

    // ------------------------------------------------------------------ the harness

    private ImportArrivals Arrivals(Action<ImportStorageOptions>? configure = null)
    {
        var options = new ImportStorageOptions { RootPath = _root };
        configure?.Invoke(options);

        return new ImportArrivals(
            new DirectoryImportFileStore(Options.Create(options), NullLogger<DirectoryImportFileStore>.Instance),
            _ledger.Repository,
            Options.Create(options),
            new FixedClock(Today),
            NullLogger<ImportArrivals>.Instance);
    }

    private DirectoryImportFileStore Store() => new(
        Options.Create(new ImportStorageOptions { RootPath = _root }), NullLogger<DirectoryImportFileStore>.Instance);

    /// <summary>As an upload arrives: forwards only, and a little at a time.</summary>
    private Task<ArrivalReport> TakeAsync(
        string source, string name, byte[] content, Action<ImportStorageOptions>? configure = null) =>
        Arrivals(configure).TakeAsync(
            source, name, new Trickle(content), "tester", "trace-1", saidDate: null, TestContext.Current.CancellationToken);

    private static byte[] Fixture(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Archives", name));

    private static byte[] Zip(params (string Path, byte[] Content)[] entries) => Zip(CompressionLevel.Optimal, entries);

    private static byte[] Zip(CompressionLevel level, params (string Path, byte[] Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in entries)
            {
                using var entry = zip.CreateEntry(path, level).Open();
                entry.Write(content);
            }
        }

        return buffer.ToArray();
    }

    private static byte[] Gzip(byte[] content)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(content);
        }

        return buffer.ToArray();
    }

    /// <summary>The files in the store proper: not what is being received or unpacked beside it.</summary>
    private List<string> StoredFiles() =>
        !Directory.Exists(_root)
            ? []
            : [.. Directory.GetFiles(_root, "*", SearchOption.AllDirectories)
                .Where(f => !Path.GetRelativePath(_root, f).StartsWith('_') && !f.EndsWith(".partial", StringComparison.Ordinal))
                .Order()];

    /// <summary>What must never outlive an arrival: a half-written file, or the copy of an archive.</summary>
    private List<string> Litter() =>
        !Directory.Exists(_root)
            ? []
            : [.. Directory.GetFiles(_root, "*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".partial", StringComparison.Ordinal)
                    || Path.GetRelativePath(_root, f).StartsWith("_incoming", StringComparison.Ordinal))];

    private void AssertNothingKept()
    {
        Assert.Empty(StoredFiles());
        Assert.Empty(Litter());
        Assert.Empty(_ledger.Files);
        Assert.Empty(_ledger.Jobs);
    }

    public void Dispose()
    {
        if (!Directory.Exists(_root))
        {
            return;
        }

        foreach (var file in Directory.GetFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_root, recursive: true);
    }

    private sealed class FixedClock(DateOnly today) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(today.ToDateTime(new TimeOnly(9, 30)), TimeSpan.Zero);
    }

    /// <summary>A stream that cannot be rewound and gives a few bytes at a time, as a connection does.</summary>
    private sealed class Trickle(byte[] content) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            // 331 bytes at most: less than the 512 looked at first, and no multiple of anything.
            var take = Math.Min(Math.Min(count, 331), content.Length - _position);
            content.AsSpan(_position, take).CopyTo(buffer.AsSpan(offset));
            _position += take;
            return take;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
