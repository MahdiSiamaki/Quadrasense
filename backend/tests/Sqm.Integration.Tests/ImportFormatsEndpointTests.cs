using System.Collections.Frozen;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sqm.Application.DataImport;
using Sqm.Application.Identity;

namespace Sqm.Integration.Tests;

/// <summary>
/// What an upload answers, now that a file may be packed and may have to wait for its date.
/// </summary>
/// <remarks>
/// Through HTTP against the real API and the real file store in a temporary folder, because two of
/// the things that can go wrong only go wrong there: a gzip is unpacked while it is still arriving,
/// and the server refuses to read a request synchronously; and what a person is told when a file is
/// refused is the response, not the exception. The repository is the remembering stand-in.
/// </remarks>
public sealed class ImportFormatsEndpointTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "sqm-formats-" + Guid.NewGuid().ToString("N"));
    private readonly ImportLedger _ledger = new();
    private readonly WebApplicationFactory<Program> _factory;

    public ImportFormatsEndpointTests(WebApplicationFactory<Program> factory) =>
        _factory = factory.WithWebHostBuilder(host =>
        {
            host.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["ImportStorage:RootPath"] = _root }));
            host.ConfigureTestServices(services =>
            {
                services.AddSingleton(TestStubs.Create<ISessionStore>((method, _) => method.Name switch
                {
                    "ResolveAsync" => Task.FromResult<ResolvedSession?>(new ResolvedSession(
                        Guid.NewGuid(),
                        new AuthenticatedUser(9, "operator", "Operator", false,
                            new[] { Permissions.ImportView, Permissions.ImportUploadSqm, Permissions.ImportUploadTac }
                                .ToFrozenSet(StringComparer.Ordinal)),
                        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1), DateTimeOffset.UtcNow.AddHours(1))),
                    "TouchAsync" => Task.CompletedTask,
                    "DeleteExpiredAsync" => Task.FromResult(0),
                    _ => throw new NotSupportedException(method.Name),
                }));
                services.AddSingleton(TestStubs.Create<IAuditLog>((_, _) => Task.CompletedTask));
                services.AddSingleton(_ledger.Repository);
            });
        });

    private static byte[] Day(int first, int rows) => Encoding.UTF8.GetBytes(
        "msisdn,imsi,imei,action\n" + string.Concat(Enumerable.Range(first, rows).Select(i => string.Create(
            CultureInfo.InvariantCulture,
            $"91200{i:00000},4321100000{i:00000},350040120{i:00000},{(i % 3 != 0 ? "add" : "remove")}\n"))));

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    [Fact]
    public async Task A_gzip_is_unpacked_as_it_arrives_and_answers_as_the_csv_would()
    {
        // Large enough to arrive in many reads: the unpacking runs while the request is still coming in.
        var day = Day(1, 40_000);

        using var response = await UploadAsync("SQM", "daily_subs_device_sim_info_2026-10-17.csv.gz", Gzip(day));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal("daily_subs_device_sim_info_2026-10-17.csv", body.GetProperty("fileName").GetString());
        Assert.Equal(Sha(day), body.GetProperty("sha256").GetString());
        Assert.Equal(day.Length, body.GetProperty("sizeBytes").GetInt64());
        Assert.Equal("2026-10-17", body.GetProperty("date").GetString());
        Assert.Equal("name", body.GetProperty("dateFrom").GetString());

        Assert.Equal(day, await File.ReadAllBytesAsync(Assert.Single(StoredFiles()), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_zip_answers_with_what_became_of_each_file_in_it()
    {
        var zip = Zip(
            ("daily_subs_device_sim_info_2026-10-17.csv", Day(1, 50)),
            ("readme.txt", "two days"u8.ToArray()),
            ("subs.csv", Day(51, 40)));

        using var response = await UploadAsync("SQM", "october.zip", zip);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal("october.zip", body.GetProperty("archive").GetString());
        Assert.Equal("zip", body.GetProperty("kind").GetString());

        var files = body.GetProperty("files").EnumerateArray().ToList();
        Assert.Equal(2, files.Count);

        Assert.Equal("queued", files[0].GetProperty("outcome").GetString());
        Assert.Equal("2026-10-17", files[0].GetProperty("date").GetString());
        Assert.Equal("name", files[0].GetProperty("dateFrom").GetString());
        Assert.Equal(100, files[0].GetProperty("jobId").GetInt64());
        Assert.Equal("Queued as import 100, dated 2026-10-17.", files[0].GetProperty("message").GetString());

        // The second has no date in its name, and an archive of two dates neither: it waits.
        Assert.Equal("waiting", files[1].GetProperty("outcome").GetString());
        Assert.Equal("missing", files[1].GetProperty("dateState").GetString());
        Assert.Equal(JsonValueKind.Null, files[1].GetProperty("jobId").ValueKind);
        Assert.Equal(
            "There is no date in the name subs.csv. It waits for someone to give it.",
            files[1].GetProperty("message").GetString());

        var skipped = Assert.Single(body.GetProperty("skipped").EnumerateArray());
        Assert.Equal("readme.txt", skipped.GetProperty("name").GetString());
        Assert.Equal("Not a .csv file.", skipped.GetProperty("reason").GetString());

        Assert.Equal(2, StoredFiles().Count);
        Assert.False(Directory.Exists(Path.Combine(_root, "_incoming")) && Directory.GetFiles(Path.Combine(_root, "_incoming")).Length > 0);
    }

    [Fact]
    public async Task The_catalogue_as_gsma_packs_it_is_unpacked_and_dated_by_the_archive()
    {
        var rar = await File.ReadAllBytesAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "Archives", "DeviceDatabaseTAC09Nov2026.rar"),
            TestContext.Current.CancellationToken);

        using var response = await UploadAsync("TAC", "DeviceDatabaseTAC09Nov2026.rar", rar);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal("rar", body.GetProperty("kind").GetString());

        var file = Assert.Single(body.GetProperty("files").EnumerateArray());
        Assert.Equal("DeviceDatabase.csv", file.GetProperty("fileName").GetString());
        Assert.Equal("queued", file.GetProperty("outcome").GetString());
        Assert.Equal("2026-11-09", file.GetProperty("date").GetString());
        Assert.Equal("archive", file.GetProperty("dateFrom").GetString());

        // The version will be labelled by this date; the job itself carries none.
        Assert.Null(Assert.Single(_ledger.Jobs).BusinessDate);
        Assert.Equal(new DateOnly(2026, 11, 9), Assert.Single(_ledger.Files).Date);
    }

    [Fact]
    public async Task A_file_that_is_refused_says_why_and_leaves_nothing()
    {
        byte[] sevenZip = [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0x00, 0x04, .. new byte[100]];

        using var response = await UploadAsync("SQM", "days.7z", sevenZip);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await JsonAsync(response);
        Assert.Equal("The file was not taken", problem.GetProperty("title").GetString());
        Assert.Equal(
            "days.7z is a 7z archive, which is not unpacked here. Send CSV, or a CSV packed as ZIP, RAR or GZ. Nothing was kept.",
            problem.GetProperty("detail").GetString());

        Assert.Empty(StoredFiles());
        Assert.Empty(_ledger.Files);
    }

    [Fact]
    public async Task A_file_with_no_date_is_kept_listed_and_queued_with_the_date_a_person_gives()
    {
        using var uploaded = await UploadAsync("SQM", "subs.csv", Day(1, 50));

        // 202: taken and kept, and not finished with.
        Assert.Equal(HttpStatusCode.Accepted, uploaded.StatusCode);
        var waiting = await JsonAsync(uploaded);
        var fileId = waiting.GetProperty("fileId").GetInt64();
        Assert.Equal("missing", waiting.GetProperty("dateState").GetString());
        Assert.Equal(0, waiting.GetProperty("dateCandidates").GetArrayLength());
        Assert.Equal(
            "There is no date in the name subs.csv. It waits for someone to give it.",
            waiting.GetProperty("message").GetString());
        Assert.Empty(_ledger.Jobs);

        using var client = _factory.CreateClient();

        // Listed at once, with what can be done about it - and without the server's own path.
        using var list = await client.SendAsync(
            Authenticated(HttpMethod.Get, "/api/v1/imports/unqueued-files"), TestContext.Current.CancellationToken);
        var listed = Assert.Single((await JsonAsync(list)).EnumerateArray());
        Assert.Equal(fileId, listed.GetProperty("fileId").GetInt64());
        Assert.Equal("missing", listed.GetProperty("dateState").GetString());
        Assert.False(listed.TryGetProperty("storedPath", out _));

        // With no date it is refused, and says what it needs.
        using var bare = await QueueAsync(client, fileId, null);
        Assert.Equal(HttpStatusCode.BadRequest, bare.StatusCode);
        Assert.Equal("This file needs its date", (await JsonAsync(bare)).GetProperty("title").GetString());

        using var notADate = await QueueAsync(client, fileId, "17/10/2026");
        Assert.Equal(HttpStatusCode.BadRequest, notADate.StatusCode);
        Assert.Equal("Not a date", (await JsonAsync(notADate)).GetProperty("title").GetString());

        using var future = await QueueAsync(client, fileId, "2062-10-17");
        Assert.Equal(HttpStatusCode.BadRequest, future.StatusCode);
        Assert.Equal("2062-10-17 has not happened yet.", (await JsonAsync(future)).GetProperty("detail").GetString());
        Assert.Empty(_ledger.Jobs);

        using var queued = await QueueAsync(client, fileId, "2026-10-17");
        Assert.Equal(HttpStatusCode.Created, queued.StatusCode);
        Assert.Equal(new DateOnly(2026, 10, 17), Assert.Single(_ledger.Jobs).BusinessDate);
        Assert.Equal(FileDateSource.Person, Assert.Single(_ledger.Files).DateSource);
        Assert.Equal("Dated 2026-10-17, by operator: its name did not settle it.", _ledger.Events[^1]);
    }

    [Theory]
    [InlineData("daily_subs_device_sim_info_2026-10-17.csv", "settled", "2026-10-17", 1)]
    [InlineData("daily_subs_device_sim_info_2026-10-17.csv.gz", "settled", "2026-10-17", 1)]
    [InlineData("subs_03-04-2026.csv", "ambiguous", null, 2)]
    [InlineData("DeviceDatabase.csv", "missing", null, 0)]
    public async Task Before_a_file_is_sent_its_name_is_read_for_its_date(
        string name, string state, string? date, int candidates)
    {
        var body = await PreflightAsync(name);

        Assert.Equal(state, body.GetProperty("dateState").GetString());
        Assert.Equal(date, body.GetProperty("date").GetString());
        Assert.Equal(candidates, body.GetProperty("dateCandidates").GetArrayLength());
    }

    [Theory]
    [InlineData("DeviceDatabaseTAC09Nov2026.rar")]
    [InlineData("october.ZIP")]
    public async Task Nothing_is_said_of_an_archives_date_before_it_is_here(string name)
    {
        // What it holds is not known until it arrives: its own name may date one file, or none.
        var body = await PreflightAsync(name);

        Assert.Equal(JsonValueKind.Null, body.GetProperty("dateState").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("date").ValueKind);
    }

    [Fact]
    public async Task A_resumable_upload_of_an_archive_is_unpacked_when_it_completes_and_its_pieces_cleared()
    {
        var zip = Zip(("daily_subs_device_sim_info_2026-10-17.csv", Day(1, 50)));
        using var client = _factory.CreateClient();

        using var completed = await ResumableAsync(client, "SQM", "october.zip", zip);

        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        var file = Assert.Single((await JsonAsync(completed)).GetProperty("files").EnumerateArray());
        Assert.Equal("queued", file.GetProperty("outcome").GetString());
        Assert.Equal(Sha(Day(1, 50)), file.GetProperty("sha256").GetString());

        Assert.Single(StoredFiles());
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "_resumable")));
    }

    [Fact]
    public async Task A_resumable_upload_that_is_refused_is_not_kept_to_be_completed_again()
    {
        var zip = Zip(("readme.txt", "nothing to import"u8.ToArray()));
        using var client = _factory.CreateClient();

        using var completed = await ResumableAsync(client, "SQM", "notes.zip", zip);

        Assert.Equal(HttpStatusCode.BadRequest, completed.StatusCode);
        Assert.Equal(
            "notes.zip holds no CSV file to import. It holds readme.txt. Nothing was kept.",
            (await JsonAsync(completed)).GetProperty("detail").GetString());

        Assert.Empty(StoredFiles());
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "_resumable")));
    }

    // ------------------------------------------------------------------ the requests

    private async Task<HttpResponseMessage> UploadAsync(string source, string name, byte[] content)
    {
        using var client = _factory.CreateClient();
        using var body = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        body.Add(file, "file", name);

        using var request = Authenticated(HttpMethod.Post, $"/api/v1/imports/{source}/upload");
        request.Content = body;
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<HttpResponseMessage> QueueAsync(HttpClient client, long fileId, string? date)
    {
        using var request = Authenticated(HttpMethod.Post, $"/api/v1/imports/SQM/unqueued-files/{fileId}/queue");
        request.Content = JsonContent.Create(new { date }, options: Web);
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private async Task<JsonElement> PreflightAsync(string name)
    {
        using var client = _factory.CreateClient();
        using var request = Authenticated(HttpMethod.Post, "/api/v1/imports/SQM/upload/preflight");
        request.Content = JsonContent.Create(new { fileName = name, sizeBytes = 10 }, options: Web);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await JsonAsync(response);
    }

    /// <summary>The tus protocol by hand: create, send every byte, complete.</summary>
    private static async Task<HttpResponseMessage> ResumableAsync(HttpClient client, string source, string name, byte[] content)
    {
        var ct = TestContext.Current.CancellationToken;

        using var create = Authenticated(HttpMethod.Post, "/api/v1/imports/uploads");
        create.Headers.Add("Tus-Resumable", "1.0.0");
        create.Headers.Add("Upload-Length", content.Length.ToString(CultureInfo.InvariantCulture));
        create.Headers.Add(
            "Upload-Metadata",
            $"source {Convert.ToBase64String(Encoding.UTF8.GetBytes(source))},filename {Convert.ToBase64String(Encoding.UTF8.GetBytes(name))}");
        using var created = await client.SendAsync(create, ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var location = created.Headers.Location!;
        var upload = location.IsAbsoluteUri ? location.AbsolutePath : location.OriginalString;

        using var patch = Authenticated(HttpMethod.Patch, upload);
        patch.Headers.Add("Tus-Resumable", "1.0.0");
        patch.Headers.Add("Upload-Offset", "0");
        patch.Content = new ByteArrayContent(content);
        patch.Content.Headers.ContentType = new("application/offset+octet-stream");
        using var sent = await client.SendAsync(patch, ct);
        Assert.Equal(HttpStatusCode.NoContent, sent.StatusCode);

        using var complete = Authenticated(HttpMethod.Post, upload + "/complete");
        complete.Headers.Add("Tus-Resumable", "1.0.0");
        return await client.SendAsync(complete, ct);
    }

    private static HttpRequestMessage Authenticated(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Cookie", "sqm_session=operator; sqm_csrf=t");
        request.Headers.Add("X-CSRF-Token", "t");
        return request;
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private static byte[] Zip(params (string Path, byte[] Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in entries)
            {
                using var entry = zip.CreateEntry(path).Open();
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

    private List<string> StoredFiles() =>
        !Directory.Exists(_root)
            ? []
            : [.. Directory.GetFiles(_root, "*", SearchOption.AllDirectories)
                .Where(f => !Path.GetRelativePath(_root, f).StartsWith('_'))];

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
}
