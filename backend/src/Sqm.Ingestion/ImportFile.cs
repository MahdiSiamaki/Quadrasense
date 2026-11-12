using System.Globalization;
using Sqm.Application.DataImport;
using Sqm.Infrastructure.DataImport;

namespace Sqm.Ingestion;

/// <summary>
/// Hands a file already on the server to the Import Center, as an upload would: the same file
/// store, the same job, the same queue and processing.
/// </summary>
/// <remarks>
/// <para>
/// <c>--import-file &lt;file&gt; [--source SQM|TAC] [--date YYYY-MM-DD]</c>. Uploads through the
/// department's VPN broke at every drop: a 370 MB day needs ~45 minutes at the ~130 KB/s measured on
/// 2026-10-25, a browser upload could not resume then, and two of two attempts broke at 12.6 and
/// 16.5 minutes, each in the minute the tunnel dropped. A file carried to the server in pieces
/// survives a drop; this queues it.
/// </para>
/// <para>
/// The file may be a CSV, or a CSV packed as GZ, ZIP or RAR (<see cref="ImportArrivals"/>): what is
/// stored is the file inside, so what is stored, and its hash, are exactly what an upload of the
/// same CSV gives, and a file sent both ways is recognised as one. The rules after storing are
/// <see cref="ImportIntake"/>'s, shared with the upload endpoint. Who may run it is whoever holds
/// the server - the endpoint's permission check has no user here, and the server's shell is already
/// beyond it.
/// </para>
/// <para>
/// A file whose name does not say its date is stored and waits, as an upload of it would; here the
/// date can be given with it, <c>--date</c>, by the person who knows.
/// </para>
/// </remarks>
internal static class ImportFile
{
    /// <summary>Who history says sent it.</summary>
    internal const string Actor = "operator (--import-file)";

    /// <summary>Stores and queues the file; returns a process exit code.</summary>
    /// <returns>
    /// 0 when every file in it is queued, or already imported; 2 for no such file, an unknown source
    /// or a date that is not one; 3 when the file is refused and nothing was kept; 4 when it is
    /// stored and a file in it waits for its date.
    /// </returns>
    public static async Task<int> RunAsync(
        ImportArrivals arrivals, string sourceCode, string path, string? date, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(arrivals);
        ArgumentNullException.ThrowIfNull(sourceCode);
        ArgumentNullException.ThrowIfNull(path);

        var source = sourceCode.ToUpperInvariant();
        if (source is not ("SQM" or "TAC"))
        {
            await Console.Error.WriteLineAsync($"unknown source '{sourceCode}': SQM or TAC").ConfigureAwait(false);
            return 2;
        }

        DateOnly? said = null;
        if (date is not null)
        {
            if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                await Console.Error.WriteLineAsync($"not a date: '{date}'. Write it as YYYY-MM-DD.").ConfigureAwait(false);
                return 2;
            }

            said = parsed;
        }

        if (!File.Exists(path))
        {
            await Console.Error.WriteLineAsync($"no such file: {path}").ConfigureAwait(false);
            return 2;
        }

        var name = Path.GetFileName(path);
        Console.WriteLine($"taking {name} into the Import Center's file store");

        ArrivalReport report;
        try
        {
            report = await arrivals
                .TakeFileAsync(source, name, path, adopt: false, Actor, correlationId: null, said, ct)
                .ConfigureAwait(false);
        }
        catch (FileRefusedException ex)
        {
            await Console.Error.WriteLineAsync($"  refused: {ex.Message}").ConfigureAwait(false);
            return 3;
        }

        var waiting = false;

        foreach (var file in report.Files)
        {
            var intake = file.Intake;
            var size = string.Create(CultureInfo.InvariantCulture, $"{file.Stored.SizeBytes:N0} bytes, sha256 {file.Stored.Sha256}");

            switch (intake.Outcome)
            {
                case IntakeOutcome.Queued:
                    Console.WriteLine(
                        $"  {file.FileName}: queued as import {intake.QueuedJobId}, dated "
                        + $"{FileDate.Text(intake.Date.Date!.Value)} ({Said(intake.Date)}) ({size})");
                    break;

                case IntakeOutcome.AlreadyImported:
                    Console.WriteLine(
                        $"  {file.FileName}: already imported as import {intake.File.ExistingJobId}; nothing queued");
                    break;

                default:
                    waiting = true;
                    Console.WriteLine(
                        $"  {file.FileName}: stored, not queued. "
                        + (intake.DateProblem ?? intake.Date.Explain(file.FileName))
                        + " Give it in the Import Center, or run this again with --date YYYY-MM-DD.");
                    break;
            }
        }

        foreach (var skipped in report.Skipped)
        {
            Console.WriteLine($"  {skipped.Name}: left out. {skipped.Reason}");
        }

        return waiting ? 4 : 0;
    }

    private static string Said(FileDateDecision date) => date.Source switch
    {
        FileDateSource.Name => "from its name",
        FileDateSource.Archive => "from its archive's name",
        FileDateSource.Person => "as given",
        _ => "as recorded",
    };
}
