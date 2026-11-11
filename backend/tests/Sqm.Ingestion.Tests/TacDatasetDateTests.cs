using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Sqm.Application.DataImport;
using Sqm.Infrastructure.DataImport;
using Sqm.Ingestion.Processing;

namespace Sqm.Ingestion.Tests;

/// <summary>A TAC version is labelled by its dataset's date, wherever that date came from.</summary>
/// <remarks>
/// GSMA's archive is named for its date and the file inside is <c>DeviceDatabase.csv</c>. On
/// 2026-11-10 the catalogue of 9 November was sent as that bare file and became version "v5124",
/// with no dataset date: the page could not say how old the catalogue was. The date is decided
/// when the file arrives and kept on it; the processor reads it from there, and from the name only
/// for a file registered before that.
/// </remarks>
public sealed class TacDatasetDateTests
{
    [Theory]
    // Given when it arrived - by its archive's name, or a person - though its own name says nothing.
    [InlineData("DeviceDatabase.csv", "2026-11-09", "v2026.11.09", "2026-11-09")]
    // The recorded date is the one decided; the name is not read again to contradict it.
    [InlineData("DeviceDatabase_TAC4Oct2026.csv", "2026-11-09", "v2026.11.09", "2026-11-09")]
    // Registered before the date was kept: the name, read by the rule the intake uses.
    [InlineData("DeviceDatabase_TAC1Sep2026.csv", null, "v2026.09.01", "2026-09-01")]
    [InlineData("DeviceDatabaseTAC09Nov2026.csv", null, "v2026.11.09", "2026-11-09")]
    [InlineData("tac_2026-09-16.csv", null, "v2026.09.16", "2026-09-16")]
    // No date anywhere: labelled by its number, as before, and dated nothing.
    [InlineData("DeviceDatabase.csv", null, "v612", null)]
    // A name that could mean two dates settles neither.
    [InlineData("tac_03-04-2026.csv", null, "v612", null)]
    public async Task The_version_carries_the_date_its_file_was_given(
        string fileName, string? declared, string label, string? datasetDate)
    {
        (string Label, DateOnly? Date)? created = null;

        var store = Stub.Create<ITacVersionStore>((method, _) => method.Name switch
        {
            "GetActiveVersionIdAsync" => Task.FromResult<int?>(null),
            "LoadVersionAsync" => Task.FromResult(2L),
            "CheckVersionAsync" => Task.FromResult(new TacVersionCheck(2, 2, 0, 0, "06-Nov-2026")),
            "ParseRadioAsync" => Task.FromResult<TacRadioParse?>(null),
            _ => Stub.Default(method),
        });

        var repository = Stub.Create<IImportJobRepository>((method, args) =>
        {
            switch (method.Name)
            {
                case "ResolveSchemaAsync":
                    return Task.FromResult(new SchemaResolution(SchemaVerdict.Known, 1, "v1", "known"));
                case "CreateTacVersionAsync":
                    created = ((string)args[2]!, (DateOnly?)args[3]);
                    return Task.CompletedTask;
                default:
                    return Stub.Default(method);
            }
        });

        var columns = ClickHouseTacVersionStore.Columns;
        string Row(string tac) => string.Join(',', columns.Select((_, i) => i == 0 ? tac : "x"));
        var file = $"{string.Join(',', columns)}\n{Row("35004012")}\n{Row("35308690")}\n";
        var context = Stub.Create<IImportContext>((method, _) => method.Name switch
        {
            "OpenFileAsync" => Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(file))),
            "EnterStageUnlessCancelledAsync" => Task.FromResult(true),
            "get_CancelledByOperator" => false,
            _ => Stub.Default(method),
        });

        var job = new ClaimedJob(
            612, "TAC", 1, "stored", fileName, "sha", file.Length, BusinessDate: null, Attempt: 1, MaxAttempts: 3,
            Priority: 0, ReprocessOfJobId: null,
            DeclaredDate: declared is null ? null : DateOnly.ParseExact(declared, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));

        var outcome = await new TacSnapshotProcessor(store, repository, NullLogger<TacSnapshotProcessor>.Instance)
            .ProcessAsync(job, context, CancellationToken.None);

        Assert.Equal(ImportJobStatus.PartiallyCompleted, outcome.Status);
        Assert.NotNull(created);
        Assert.Equal(label, created.Value.Label);
        Assert.Equal(
            datasetDate,
            created.Value.Date?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
    }
}
