using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sqm.Application.DataImport;
using Sqm.Application.Quality;
using Sqm.Ingestion.Processing;

namespace Sqm.Ingestion.Tests;

/// <summary>Which day a daily file is imported as.</summary>
/// <remarks>
/// The day is decided when the file arrives and the job carries it. The processor's two fallbacks
/// are for jobs queued before that was so: the date recorded on the file, then the name - read by
/// the rule the intake uses, so that a job can never be imported as a day the queue did not place
/// it by. A file with no day anywhere is refused rather than guessed at.
/// </remarks>
public sealed class DailyBusinessDateTests
{
    private const string File = "msisdn,imsi,imei,label\n9120000001,432110000000001,35004012000001,add\n";

    [Theory]
    // The job's own date: what the queue ordered it by. Nothing else is consulted.
    [InlineData("2026-06-15", "2026-06-14", "daily_subs_device_sim_info_2026-06-13.csv", "2026-06-15")]
    // A job queued without one: the date recorded on its file.
    [InlineData(null, "2026-06-14", "daily_subs_device_sim_info_2026-06-13.csv", "2026-06-14")]
    // Neither: the name, in any of the ways a name writes one date.
    [InlineData(null, null, "daily_subs_device_sim_info_2026-06-13.csv", "2026-06-13")]
    [InlineData(null, null, "subs_13-06-2026.csv", "2026-06-13")]
    [InlineData(null, null, "subs_13Jun2026.csv", "2026-06-13")]
    public async Task The_day_is_the_jobs_then_the_files_then_the_names(
        string? jobDate, string? fileDate, string name, string expected)
    {
        var asked = await DayAskedAboutAsync(jobDate, fileDate, name);

        Assert.Equal(expected, asked?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("subs.csv")]
    // Could be 3 April or 4 March: importing it as either would be a guess.
    [InlineData("subs_03-04-2026.csv")]
    [InlineData("dump_subs_device_sim_info_20251227_20260125.csv")]
    public async Task A_file_with_no_one_day_anywhere_is_refused_and_not_guessed_at(string name)
    {
        var refusal = await Assert.ThrowsAsync<ImportRejectedException>(() => DayAskedAboutAsync(null, null, name));

        Assert.Equal(
            $"Cannot determine which day '{name}' describes: its name does not say one date, and none was given for it.",
            refusal.Message);
    }

    /// <summary>Runs the processor as far as its first question about a day, and returns the day asked about.</summary>
    private static async Task<DateOnly?> DayAskedAboutAsync(string? jobDate, string? fileDate, string name)
    {
        DateOnly? asked = null;

        var analytics = Stub.Create<IAnalyticsIngestionStore>((method, args) =>
        {
            if (method.Name == "CountEventsForDateAsync")
            {
                asked = (DateOnly)args[0]!;
                throw new OperationCanceledException("the day is known: stop here");
            }

            return Stub.Default(method);
        });

        var repository = Stub.Create<IImportJobRepository>((m, _) => m.Name == "ResolveSchemaAsync"
            ? Task.FromResult(new SchemaResolution(SchemaVerdict.Known, 1, "v1", "known"))
            : Stub.Default(m));

        var processor = new SqmDailyProcessor(
            analytics,
            repository,
            new DashboardSnapshot(
                analytics, repository,
                new MartRefresh(analytics, Stub.Create<ITacVersionStore>((m, _) => Stub.Default(m)), NullLogger<MartRefresh>.Instance),
                NullLogger<DashboardSnapshot>.Instance),
            new FeedQualityMonitor(
                analytics, Stub.Create<IFeedQualityReader>((m, _) => Stub.Default(m)),
                Options.Create(new FeedQualityOptions()), NullLogger<FeedQualityMonitor>.Instance),
            new RiskDayStep(analytics, NullLogger<RiskDayStep>.Instance),
            new TacDayStep(analytics, NullLogger<TacDayStep>.Instance),
            NullLogger<SqmDailyProcessor>.Instance);

        var context = Stub.Create<IImportContext>((m, _) => m.Name switch
        {
            "OpenFileAsync" => Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(File))),
            "IsCancellationRequestedAsync" => Task.FromResult(false),
            "EnterStageUnlessCancelledAsync" => Task.FromResult(true),
            "get_CancelledByOperator" => false,
            _ => Stub.Default(m),
        });

        static DateOnly? Parse(string? text) =>
            text is null ? null : DateOnly.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture);

        var job = new ClaimedJob(
            1, "SQM", 1, "stored", name, "sha", File.Length, Parse(jobDate), 1, 3, 0, null, DeclaredDate: Parse(fileDate));

        try
        {
            await processor.ProcessAsync(job, context, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // Stopped where the day became known.
        }

        return asked;
    }
}
