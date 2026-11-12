using Sqm.Application.DataImport;

namespace Sqm.Integration.Tests;

/// <summary>
/// A TAC version's row is its own: a second dataset of the same date gets its own label, and
/// never the first one's row.
/// </summary>
/// <remarks>
/// <para>
/// A version is labelled by its dataset's date. Until 2026-11-12 a second version with the same
/// label took over the first one's row - pointed it at its own data and set it READY, whatever it
/// had been. Had the first been the active version, the record would have said that nothing was
/// active while the analytics store went on serving it. It needed two different files of one date,
/// which was rare while a date came only from a file's name; a date can now be typed (ADR-025).
/// </para>
/// <para>
/// Against a real PostgreSQL, because the rule is a statement's conflict clause. Nothing here
/// activates a version: the database under test is the development one, and it has a real active
/// version this must not touch.
/// </para>
/// </remarks>
[Collection("import-queue")]
public sealed class TacVersionLabelTests(ImportQueueFixture fixture) : IClassFixture<ImportQueueFixture>
{
    private const string TestSource = "ITTACLBL";

    private async Task<long> NewJobAsync(string seed, CancellationToken ct)
    {
        var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("tac-label-" + seed)));
        var file = await fixture.Repository.RegisterFileAsync(
            TestSource, seed + ".csv", new StoredFile($"itest/{sha[..8]}.csv", 1024, sha), "tester", ct);
        return await fixture.Repository.EnqueueAsync(TestSource, file.FileId, null, "tester", ct: ct);
    }

    private async Task<TacVersion> VersionOfAsync(long jobId, CancellationToken ct) =>
        Assert.Single(await fixture.Repository.ListTacVersionsAsync(ct), v => v.JobId == jobId);

    [Fact]
    public async Task A_second_dataset_of_the_same_date_is_its_own_version_under_its_own_label()
    {
        if (!fixture.IsAvailable)
        {
            Assert.Skip(fixture.UnavailableReason ?? "no database");
        }

        var ct = TestContext.Current.CancellationToken;
        await fixture.CleanupAsync(TestSource);
        await fixture.EnsureSourceAsync(TestSource);

        // A label no real version can hold.
        var label = "vTEST." + Guid.NewGuid().ToString("N")[..10];
        var date = new DateOnly(2026, 11, 9);

        try
        {
            var first = await NewJobAsync("first", ct);
            await fixture.Repository.CreateTacVersionAsync(first, (int)first, label, date, 100, null, null, ct);

            var original = await VersionOfAsync(first, ct);
            Assert.Equal(label, original.VersionLabel);
            Assert.Equal(TacVersionStatus.Ready, original.Status);
            Assert.Equal(100, original.RowCount);

            // The same job again - a retry after a crash - rewrites its own row and nothing else.
            await fixture.Repository.CreateTacVersionAsync(
                first, (int)first, label, date, 101, null, new TacVersionDiff(1, 2, 3, 4, 5), ct);

            var retried = await VersionOfAsync(first, ct);
            Assert.Equal(original.Id, retried.Id);
            Assert.Equal(label, retried.VersionLabel);
            Assert.Equal(101, retried.RowCount);
            Assert.Equal(1, retried.TacsAdded);

            // Another file of the same date: its own version, and the first is as it was.
            var second = await NewJobAsync("second", ct);
            await fixture.Repository.CreateTacVersionAsync(second, (int)second, label, date, 200, null, null, ct);

            var another = await VersionOfAsync(second, ct);
            Assert.NotEqual(original.Id, another.Id);
            Assert.Equal(label + " (2)", another.VersionLabel);
            Assert.Equal(200, another.RowCount);
            Assert.Equal(date, another.DatasetDate);

            var untouched = await VersionOfAsync(first, ct);
            Assert.Equal(original.Id, untouched.Id);
            Assert.Equal(label, untouched.VersionLabel);
            Assert.Equal(101, untouched.RowCount);

            // A third is the third, and a retry of the second is still the second.
            var third = await NewJobAsync("third", ct);
            await fixture.Repository.CreateTacVersionAsync(third, (int)third, label, date, 300, null, null, ct);
            Assert.Equal(label + " (3)", (await VersionOfAsync(third, ct)).VersionLabel);

            await fixture.Repository.CreateTacVersionAsync(second, (int)second, label, date, 201, null, null, ct);
            var secondAgain = await VersionOfAsync(second, ct);
            Assert.Equal(another.Id, secondAgain.Id);
            Assert.Equal(label + " (2)", secondAgain.VersionLabel);
            Assert.Equal(201, secondAgain.RowCount);
        }
        finally
        {
            await fixture.CleanupAsync(TestSource);
        }
    }
}
