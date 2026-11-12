using Sqm.Application.DataImport;

namespace Sqm.Integration.Tests;

/// <summary>
/// A header the platform has not seen before, against a real PostgreSQL.
/// </summary>
/// <remarks>
/// Every other test of <c>ResolveSchemaAsync</c> is a stub that answers "known", so the one path
/// that reads the contract on record had never run against a database. It could not: the row it
/// reads has an array column, and no constructor matched it. On 2026-11-10 a <c>.rar</c> was sent as
/// a TAC file and the import failed three times with a message about a constructor, where it should
/// have said the file was not the catalogue. That is the case below.
/// </remarks>
[Collection("import-queue")]
public sealed class SchemaResolutionTests(ImportQueueFixture fixture) : IClassFixture<ImportQueueFixture>
{
    private const string TestSource = "ITSCHEMA";

    private static readonly string[] Contract = ["msisdn", "imsi", "imei", "status"];

    private async Task<long> NewJobAsync(string seed, CancellationToken ct)
    {
        var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("schema-" + seed)));
        var file = await fixture.Repository.RegisterFileAsync(
            TestSource, seed + ".csv", new StoredFile($"itest/{sha[..8]}.csv", 1024, sha), "tester", ct);
        return await fixture.Repository.EnqueueAsync(TestSource, file.FileId, null, "tester", ct: ct);
    }

    [Fact]
    public async Task A_header_not_on_record_is_compared_with_the_contract_and_not_failed_on()
    {
        if (!fixture.IsAvailable)
        {
            Assert.Skip(fixture.UnavailableReason ?? "no database");
        }

        var ct = TestContext.Current.CancellationToken;
        await fixture.CleanupAsync(TestSource);
        await fixture.EnsureSourceAsync(TestSource);

        try
        {
            // The first file a source delivers is its contract.
            var first = await fixture.Repository.ResolveSchemaAsync(
                await NewJobAsync("first", ct), TestSource, Contract, ct);
            Assert.Equal(SchemaVerdict.Known, first.Verdict);
            Assert.Equal("v1", first.VersionLabel);

            // The same header again, in other letters: the same contract.
            var again = await fixture.Repository.ResolveSchemaAsync(
                await NewJobAsync("again", ct), TestSource, ["MSISDN", " imsi ", "IMEI", "status"], ct);
            Assert.Equal(SchemaVerdict.Known, again.Verdict);
            Assert.Equal(first.SchemaVersionId, again.SchemaVersionId);

            // Not the file at all - what a .rar's first line looks like to a CSV reader.
            var garbage = await fixture.Repository.ResolveSchemaAsync(
                await NewJobAsync("garbage", ct), TestSource, ["Rar!\u001a\u0007\u0001"], ct);
            Assert.Equal(SchemaVerdict.Rejected, garbage.Verdict);
            Assert.Null(garbage.SchemaVersionId);
            Assert.Contains("missing 4 column(s)", garbage.Explanation, StringComparison.Ordinal);

            // Every column there, two of them swapped.
            var reordered = await fixture.Repository.ResolveSchemaAsync(
                await NewJobAsync("reordered", ct), TestSource, ["msisdn", "imei", "imsi", "status"], ct);
            Assert.Equal(SchemaVerdict.Rejected, reordered.Verdict);
            Assert.Contains("Column 2 is 'imei'", reordered.Explanation, StringComparison.Ordinal);

            // A column added at the end: a new version, recorded, and the job says so.
            var extendedJob = await NewJobAsync("extended", ct);
            var extended = await fixture.Repository.ResolveSchemaAsync(
                extendedJob, TestSource, [.. Contract, "esim"], ct);
            Assert.Equal(SchemaVerdict.AcceptedWithWarning, extended.Verdict);
            Assert.Equal("v2", extended.VersionLabel);
            Assert.NotEqual(first.SchemaVersionId, extended.SchemaVersionId);

            var detail = await fixture.Repository.GetJobAsync(extendedJob, ct);
            Assert.NotNull(detail);
            Assert.Equal("v2", detail.SchemaVersion);
            Assert.Contains(detail.Events, e => e.Message.StartsWith("New schema version v2.", StringComparison.Ordinal));

            // v2 is now the contract: the next new header is compared with it, not with v1.
            var further = await fixture.Repository.ResolveSchemaAsync(
                await NewJobAsync("further", ct), TestSource, [.. Contract, "esim", "volte"], ct);
            Assert.Equal(SchemaVerdict.AcceptedWithWarning, further.Verdict);
            Assert.Equal("v3", further.VersionLabel);
            Assert.Contains("adds 1 new column(s) at the end: volte", further.Explanation, StringComparison.Ordinal);
        }
        finally
        {
            await fixture.CleanupAsync(TestSource);
        }
    }
}
