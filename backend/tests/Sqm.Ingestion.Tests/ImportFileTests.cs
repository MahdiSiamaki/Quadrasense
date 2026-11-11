using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sqm.Application.DataImport;
using Sqm.Infrastructure.DataImport;

namespace Sqm.Ingestion.Tests;

/// <summary>A file already on the server enters the Import Center exactly as an upload of it would.</summary>
/// <remarks>
/// Uploads through the department's VPN broke at every drop (2026-10-25), so days are carried to the
/// server in pieces and queued with --import-file. These drive it against stubs; the rules after
/// storing are ImportIntake's, which the upload endpoint shares and Sqm.Integration.Tests'
/// OrphanedUploadTests exercises through HTTP. What is unpacked here is real: the archives are made
/// in the test and read by the readers that ship.
/// </remarks>
public sealed class ImportFileTests : IDisposable
{
    private const string Day = "msisdn,imsi,imei,action\n9120000001,432110000000001,35004012000001,add\n";

    private const string Catalogue = "tac,manufacturer,model\n35000001,Test Maker,Model 1\n";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "importfile-" + Guid.NewGuid().ToString("N"));

    public ImportFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task A_compressed_day_is_stored_as_its_csv_and_queued_for_the_day_in_its_name()
    {
        var fake = new Fakes();
        var path = Path.Combine(_dir, "daily_subs_device_sim_info_2026-01-26.csv.gz");
        await using (var gz = new GZipStream(File.Create(path), CompressionLevel.Fastest))
        {
            await gz.WriteAsync(Encoding.UTF8.GetBytes(Day));
        }

        var exit = await Run(fake, "SQM", path);

        Assert.Equal(0, exit);
        Assert.Equal([("SQM", "daily_subs_device_sim_info_2026-01-26.csv", Day)], fake.Saved);
        Assert.Equal([("SQM", new DateOnly(2026, 1, 26), ImportFile.Actor)], fake.Enqueued);
        Assert.Equal(
            ["SaveAsync", "RegisterFileAsync", "EnqueueAsync", "AppendEventAsync", "WriteAuditAsync:import.upload"],
            fake.Calls);

        // The date is recorded on the file with who gave it, and written where a person can see it.
        var registered = Assert.Single(fake.Registered);
        Assert.Null(registered.Archive);
        Assert.Equal(new DateOnly(2026, 1, 26), registered.Date?.Date);
        Assert.Equal(FileDateSource.Name, registered.Date?.Source);
        Assert.Equal(["Dated 2026-01-26, from its name."], fake.Events);
    }

    [Fact]
    public async Task Content_already_imported_is_not_queued_again_and_its_second_copy_is_removed()
    {
        var fake = new Fakes { Known = true, ExistingJobId = 7 };

        var exit = await Run(fake, "SQM", Write("daily_subs_device_sim_info_2026-01-26.csv", Day));

        Assert.Equal(0, exit);
        Assert.Equal(
            ["SaveAsync", "RegisterFileAsync", "DeleteAsync", "WriteAuditAsync:import.upload.duplicate"], fake.Calls);
        Assert.Empty(fake.Enqueued);
    }

    [Fact]
    public async Task A_file_registered_without_a_job_is_queued_from_the_copy_already_stored()
    {
        // An upload that ended between registering the file and queueing it.
        var fake = new Fakes { Known = true, ExistingJobId = null };

        var exit = await Run(fake, "SQM", Write("daily_subs_device_sim_info_2026-01-26.csv", Day));

        Assert.Equal(0, exit);
        Assert.Equal(
            [
                "SaveAsync", "RegisterFileAsync", "DeleteAsync", "SetFileDateAsync", "EnqueueAsync", "AppendEventAsync",
                "WriteAuditAsync:import.upload",
            ],
            fake.Calls);
    }

    [Fact]
    public async Task An_unknown_source_is_refused_before_anything_is_stored()
    {
        var fake = new Fakes();

        var exit = await Run(fake, "XYZ", Write("daily_subs_device_sim_info_2026-01-26.csv", Day));

        Assert.Equal(2, exit);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task A_file_whose_name_says_no_date_is_stored_and_waits_for_one()
    {
        // The catalogue as GSMA names the file inside its archive: on 2026-11-10 it was sent like
        // this, and became a version labelled by its job number with no date at all.
        var fake = new Fakes();

        var exit = await Run(fake, "TAC", Write("DeviceDatabase.csv", Catalogue));

        Assert.Equal(4, exit);
        Assert.Equal(["SaveAsync", "RegisterFileAsync", "WriteAuditAsync:import.upload.undated"], fake.Calls);
        Assert.Empty(fake.Enqueued);
        Assert.Null(Assert.Single(fake.Registered).Date);
    }

    [Fact]
    public async Task The_date_can_be_given_with_the_file_by_whoever_knows_it()
    {
        var fake = new Fakes();

        var exit = await Run(fake, "TAC", Write("DeviceDatabase.csv", Catalogue), "2026-11-09");

        Assert.Equal(0, exit);

        var registered = Assert.Single(fake.Registered);
        Assert.Equal(new DateOnly(2026, 11, 9), registered.Date?.Date);
        Assert.Equal(FileDateSource.Person, registered.Date?.Source);

        // A snapshot's job carries no business date: with one it would wait behind every earlier
        // dataset that failed. Its date is on the file.
        Assert.Equal([("TAC", null, ImportFile.Actor)], fake.Enqueued);
        Assert.Equal([$"Dated 2026-11-09, by {ImportFile.Actor}: its name did not settle it."], fake.Events);
    }

    [Fact]
    public async Task A_date_the_name_contradicts_is_not_taken_and_nothing_is_queued()
    {
        var fake = new Fakes();

        var exit = await Run(
            fake, "SQM", Write("daily_subs_device_sim_info_2026-01-26.csv", Day), "2026-01-27");

        // Neither date is acted on: the one given is refused, and the name's is not what was asked for.
        Assert.Equal(4, exit);
        Assert.Empty(fake.Enqueued);
        Assert.Contains("WriteAuditAsync:import.upload.undated", fake.Calls);
    }

    [Theory]
    [InlineData("26-01-2026")]
    [InlineData("2026-13-01")]
    [InlineData("yesterday")]
    public async Task What_is_not_a_date_is_refused_before_anything_is_stored(string date)
    {
        var fake = new Fakes();

        var exit = await Run(fake, "SQM", Write("daily_subs_device_sim_info_2026-01-26.csv", Day), date);

        Assert.Equal(2, exit);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task A_zip_is_unpacked_and_each_csv_in_it_is_queued_under_its_own_name_and_day()
    {
        var fake = new Fakes();
        var path = Zip(
            "october.zip",
            ("daily_subs_device_sim_info_2026-10-17.csv", Day),
            ("readme.txt", "two days"),
            ("days/daily_subs_device_sim_info_2026-10-18.csv", Day + "9120000002,432110000000002,35004012000002,remove\n"));

        var exit = await Run(fake, "SQM", path);

        Assert.Equal(0, exit);
        Assert.Equal(
            ["daily_subs_device_sim_info_2026-10-17.csv", "daily_subs_device_sim_info_2026-10-18.csv"],
            fake.Saved.Select(s => s.Name));
        Assert.Equal(Day, fake.Saved[0].Content);
        Assert.Equal(
            [("SQM", new DateOnly(2026, 10, 17), ImportFile.Actor), ("SQM", new DateOnly(2026, 10, 18), ImportFile.Actor)],
            fake.Enqueued);

        // Both are unpacked and stored before either is registered: an archive is taken whole.
        Assert.Equal(["SaveAsync", "SaveAsync", "RegisterFileAsync"], fake.Calls.Take(3));
        Assert.All(fake.Registered, r => Assert.Equal("october.zip", r.Archive));
    }

    [Fact]
    public async Task The_only_file_of_an_archive_takes_its_date_from_the_archives_name()
    {
        // GSMA's delivery: the date is on the archive, the file inside is DeviceDatabase.csv.
        var fake = new Fakes();

        var exit = await Run(fake, "TAC", Zip("DeviceDatabaseTAC09Nov2026.zip", ("DeviceDatabase.csv", Catalogue)));

        Assert.Equal(0, exit);

        var registered = Assert.Single(fake.Registered);
        Assert.Equal("DeviceDatabase.csv", registered.Name);
        Assert.Equal("DeviceDatabaseTAC09Nov2026.zip", registered.Archive);
        Assert.Equal(new DateOnly(2026, 11, 9), registered.Date?.Date);
        Assert.Equal(FileDateSource.Archive, registered.Date?.Source);
        Assert.Equal(
            ["Dated 2026-11-09, from the name of the archive it arrived in, DeviceDatabaseTAC09Nov2026.zip."],
            fake.Events);
    }

    [Fact]
    public async Task An_archive_with_no_csv_in_it_is_refused_and_nothing_is_kept()
    {
        var fake = new Fakes();

        var exit = await Run(fake, "SQM", Zip("notes.zip", ("readme.txt", "nothing here"), ("sheet.xlsx", "PK")));

        Assert.Equal(3, exit);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task One_date_given_for_an_archive_of_several_files_is_refused()
    {
        var fake = new Fakes();
        var path = Zip("two.zip", ("a.csv", Day), ("b.csv", Day + "9120000002,432110000000002,35004012000002,add\n"));

        var exit = await Run(fake, "SQM", path, "2026-10-17");

        Assert.Equal(3, exit);
        Assert.Empty(fake.Calls);
    }

    private Task<int> Run(Fakes fake, string source, string path, string? date = null)
    {
        var arrivals = new ImportArrivals(
            fake.Files,
            fake.Repository,
            Options.Create(new ImportStorageOptions { RootPath = _dir }),
            TimeProvider.System,
            NullLogger<ImportArrivals>.Instance);

        return ImportFile.RunAsync(arrivals, source, path, date, CancellationToken.None);
    }

    private string Write(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private string Zip(string name, params (string Path, string Content)[] entries)
    {
        var path = Path.Combine(_dir, name);
        using var zip = new ZipArchive(File.Create(path), ZipArchiveMode.Create);

        foreach (var (entryPath, content) in entries)
        {
            using var entry = zip.CreateEntry(entryPath).Open();
            entry.Write(Encoding.UTF8.GetBytes(content));
        }

        return path;
    }

    private sealed class Fakes
    {
        public Fakes()
        {
            Files = Stub.Create<IImportFileStore>((method, args) =>
            {
                Calls.Add(method.Name);
                return method.Name == "SaveAsync"
                    ? SaveAsync((string)args[0]!, (string)args[1]!, (Stream)args[2]!)
                    : Stub.Default(method);
            });

            Repository = Stub.Create<IImportJobRepository>((method, args) =>
            {
                switch (method.Name)
                {
                    case "RegisterFileAsync":
                        Calls.Add(method.Name);
                        Registered.Add(((string)args[1]!, (string?)args[4], (FileDateDecision?)args[5]));
                        return Task.FromResult(new RegisteredFile(
                            40 + Registered.Count, "abc", IsNew: !Known, Known ? ExistingJobId : null, null));
                    case "EnqueueAsync":
                        Calls.Add(method.Name);
                        Enqueued.Add(((string)args[0]!, (DateOnly?)args[2], (string)args[3]!));
                        return Task.FromResult(8L + Enqueued.Count);
                    case "AppendEventAsync":
                        Calls.Add(method.Name);
                        Events.Add((string)args[3]!);
                        return Task.CompletedTask;
                    case "WriteAuditAsync":
                        Calls.Add($"{method.Name}:{args[1]}");
                        return Task.CompletedTask;
                    default:
                        Calls.Add(method.Name);
                        return Stub.Default(method);
                }
            });
        }

        public IImportFileStore Files { get; }

        public IImportJobRepository Repository { get; }

        public bool Known { get; init; }

        public long? ExistingJobId { get; init; }

        public List<string> Calls { get; } = [];

        public List<(string Source, string Name, string Content)> Saved { get; } = [];

        public List<(string Name, string? Archive, FileDateDecision? Date)> Registered { get; } = [];

        public List<(string Source, DateOnly? Date, string By)> Enqueued { get; } = [];

        public List<string> Events { get; } = [];

        private async Task<StoredFile> SaveAsync(string source, string name, Stream content)
        {
            using var reader = new StreamReader(content);
            var text = await reader.ReadToEndAsync();
            Saved.Add((source, name, text));
            return new StoredFile($"SQM/2026/10/stored-{Saved.Count}.csv", text.Length, "abc" + Saved.Count);
        }
    }
}
