using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Sqm.Api.Infrastructure;
using Sqm.Application.DataImport;

using Sqm.Api.Auth;
using Sqm.Application.Identity;
using Sqm.Infrastructure.DataImport;

namespace Sqm.Api.Endpoints;

/// <summary>Import Center endpoints: upload, history, detail, operator actions.</summary>
public static class ImportEndpoints
{
    /// <summary>Registers the import routes.</summary>
    public static IEndpointRouteBuilder MapImportEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Per route rather than per group: reading import history and submitting a gigabyte of
        // subscriber data are not the same risk, and a Data Operator has one without the other.
        var group = app.MapGroup("/api/v1/imports").WithTags("Imports")
            .RequireAuthorization(PermissionPolicyProvider.Prefix + Permissions.ImportView);

        // The source code decides which upload permission applies, so it is checked inside the
        // handler rather than declared here - see UploadAsync.
        group.MapPost("/{sourceCode}/upload", UploadAsync)
            .WithName("UploadImportFile")
            .WithSummary("Uploads a CSV, or one packed as GZ, ZIP or RAR, and queues what it holds for import.")
            .DisableAntiforgery()
            .WithMetadata(new DisableRequestSizeLimitAttribute());

        // Asks the same question the upload asks, without the body.
        //
        // The server already refuses an unauthorised upload before reading a byte - but the
        // BROWSER does not know that, and pushes the whole file at it regardless. A 319.6 MB
        // daily file was sent and refused in 0.4 ms, which is 319.6 MB across the wire to learn
        // something a 200-byte request could have said first.
        group.MapPost("/{sourceCode}/upload/preflight", PreflightAsync)
            .WithName("PreflightImportUpload")
            .WithSummary("Whether this file would be accepted, asked before sending it.");

        group.MapGet("/", ListAsync)
            .WithName("ListImports")
            .WithSummary("Import history, newest first.");

        group.MapGet("/{jobId:long}", GetAsync)
            .WithName("GetImport")
            .WithSummary("One import: timeline, progress, quarantine and lineage.");

        group.MapGet("/{jobId:long}/preview", PreviewAsync)
            .WithName("PreviewImportFile")
            .WithSummary("The first rows of the stored file, exactly as delivered.");

        group.MapGet("/{jobId:long}/quarantine/{summaryId:long}", GetQuarantineSamplesAsync)
            .WithName("GetQuarantineSamples")
            .WithSummary("Example rows for one quarantine rule.");

        group.MapPost("/{jobId:long}/cancel", CancelAsync)
            .RequireAuthorization(PermissionPolicyProvider.Prefix + Permissions.ImportCancel)
            .WithName("CancelImport")
            .WithSummary("Asks a queued or running import to stop at the next stage boundary.");

        group.MapPost("/{jobId:long}/reprocess", ReprocessAsync)
            .RequireAuthorization(PermissionPolicyProvider.Prefix + Permissions.ImportReprocess)
            .WithName("ReprocessImport")
            .WithSummary("Queues the same stored file again as a new job.");

        group.MapGet("/freshness", GetFreshnessAsync)
            .WithName("GetImportFreshness")
            .WithSummary("How current each source is, and which expected days are missing.");

        group.MapGet("/worker-health", GetWorkerHealthAsync)
            .WithName("GetWorkerHealth")
            .WithSummary("Queue depth, running jobs, failures and stale leases.");

        group.MapGet("/blockages", GetBlockagesAsync)
            .WithName("GetImportBlockages")
            .WithSummary("Failed or cancelled days that hold later queued days back, with nothing queued to land them.");

        group.MapGet("/unqueued-files", GetUnqueuedFilesAsync)
            .WithName("GetUnqueuedFiles")
            .WithSummary("Stored files with no import: waiting for their date, or left before the queue.");

        // The rest of an upload, so the upload's permission, which the source decides - as for
        // UploadAsync, it is checked inside the handler.
        group.MapPost("/{sourceCode}/unqueued-files/{fileId:long}/queue", QueueUnqueuedFileAsync)
            .WithName("QueueUnqueuedFile")
            .WithSummary("Queues a stored file that has no import, with the date a person gives it when its name does not say.");

        group.MapGet("/stage-durations", GetStageDurationsAsync)
            .WithName("GetImportStageDurations")
            .WithSummary("How long each stage usually takes for a source - what the time left is estimated from.");

        return app;
    }

    private static async Task<IResult> UploadAsync(
        string sourceCode,
        ImportArrivals arrivals,
        IAuditLog audit,
        HttpContext http,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);

        var source = sourceCode.ToUpperInvariant();

        // The same decision the preflight made, made again. The client is expected to have asked
        // first, but the client is not what enforces this - a caller that skips the preflight,
        // or whose permission was withdrawn between the two requests, is refused here.
        var refusal = await RefuseUploadAsync(sourceCode, http, audit, ct).ConfigureAwait(false);
        if (refusal is not null)
        {
            return refusal;
        }

        if (!StreamedUpload.IsMultipart(http.Request))
        {
            return Results.Problem(
                title: "Not a file upload",
                detail: "Send the file as a multipart/form-data request.",
                statusCode: StatusCodes.Status415UnsupportedMediaType);
        }

        // The bytes are streamed from the socket and hashed on the way past - into the file store
        // for a CSV or a gzip, to a copy on the store's volume for an archive, which is unpacked from
        // there. Binding an IFormFile would have the framework buffer the whole body first, writing
        // a gigabyte import to disk twice. What is stored, the duplicate rule, the date and the
        // queueing are ImportArrivals' and ImportIntake's: --import-file follows the same rules.
        ArrivalReport? report;
        try
        {
            report = await StreamedUpload.ReadFileSectionAsync(
                http.Request,
                (fileName, body, token) => arrivals.TakeAsync(
                    source, fileName, body, CurrentActor(http), http.TraceIdentifier, saidDate: null, token),
                ct).ConfigureAwait(false);
        }
        catch (FileRefusedException ex)
        {
            return Refused(ex);
        }

        return report is null
            ? Results.Problem(
                title: "No file",
                detail: "The request carried no file.",
                statusCode: StatusCodes.Status400BadRequest)
            : RespondToArrival(report);
    }

    /// <summary>A file that cannot be taken, with the reason its sender can act on.</summary>
    internal static IResult Refused(FileRefusedException refusal)
    {
        ArgumentNullException.ThrowIfNull(refusal);

        return Results.Problem(
            title: "The file was not taken",
            detail: refusal.Message,
            statusCode: StatusCodes.Status400BadRequest);
    }

    /// <summary>
    /// Answers for something that arrived, as every upload answers. Shared with resumable uploads
    /// (ADR-018).
    /// </summary>
    /// <remarks>
    /// One file - a CSV, or a gzip of one - answers as it always has: 201 with its job, or 409 with
    /// the import it duplicates; and 202 when it is stored and waits for its date. An archive
    /// answers 200 with what became of each file in it, because several things can be true at once.
    /// </remarks>
    internal static IResult RespondToArrival(ArrivalReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        if (report.IsArchive)
        {
            return Results.Ok(new ArchiveUploadResponse(
                report.FileName,
                report.Kind.ToString().ToLowerInvariant(),
                [.. report.Files.Select(ArchivedFileResponse.Of)],
                [.. report.Skipped.Select(s => new SkippedEntryResponse(s.Name, s.Reason))]));
        }

        var file = report.Files[0];
        var intake = file.Intake;

        return intake.Outcome switch
        {
            IntakeOutcome.Queued => Results.Created(
                $"/api/v1/imports/{intake.QueuedJobId}",
                new UploadAcceptedResponse(
                    intake.QueuedJobId!.Value, intake.File.FileId, file.FileName, file.Stored.SizeBytes,
                    file.Stored.Sha256, DateText(intake.Date), DateSourceText(intake.Date))),

            // 409, not an error page. A duplicate upload is a normal thing for an operator to do
            // - the same file sent twice, or a retry after a lost connection - and the useful
            // response is "here is the import you already have", not "something went wrong".
            IntakeOutcome.AlreadyImported => Results.Conflict(new DuplicateUploadResponse(
                intake.File.FileId,
                intake.File.ExistingJobId,
                intake.File.ExistingOriginalName ?? file.FileName,
                file.Stored.Sha256,
                "This file's content has already been uploaded for this source.")),

            // 202: taken and kept, and not finished with. It is on the list the Import Center
            // shows, where its date is given.
            _ => Results.Accepted(
                "/api/v1/imports/unqueued-files",
                new UploadWaitingResponse(
                    intake.File.FileId, file.FileName, file.Stored.SizeBytes, file.Stored.Sha256,
                    DateStateText(intake.Date), [.. intake.Date.Candidates.Select(FileDate.Text)],
                    intake.DateProblem ?? intake.Date.Explain(file.FileName))),
        };
    }

    internal static string? DateText(FileDateDecision date) =>
        date is { IsSettled: true, Date: { } day } ? FileDate.Text(day) : null;

    /// <summary>Who settled the date, as the API writes it: name, archive, person or recorded.</summary>
    internal static string? DateSourceText(FileDateDecision date) =>
        date.IsSettled ? date.Source?.ToString().ToLowerInvariant() ?? "recorded" : null;

    /// <summary>settled, ambiguous, several, missing or future.</summary>
    internal static string DateStateText(FileDateDecision date) => date.State.ToString().ToLowerInvariant();

    private static async Task<IResult> ListAsync(
        IImportJobRepository repository,
        CancellationToken ct,
        string? source = null,
        string? status = null,
        string? from = null,
        string? to = null,
        string? fileName = null,
        bool effectiveOnly = false,
        int page = 1,
        int pageSize = 25)
    {
        var statuses = ParseStatuses(status);
        if (statuses is null)
        {
            return Results.Problem(
                title: "Unknown status",
                detail: $"'{status}' is not an import status.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var filter = new ImportHistoryFilter(
            string.IsNullOrWhiteSpace(source) ? null : source.ToUpperInvariant(),
            statuses.Count == 0 ? null : statuses,
            ParseDate(from),
            ParseDate(to),
            fileName,
            effectiveOnly);

        var size = Math.Clamp(pageSize, 1, 200);
        var offset = (Math.Max(1, page) - 1) * size;

        var items = await repository.ListJobsAsync(filter, size, offset, ct).ConfigureAwait(false);
        var total = await repository.CountJobsAsync(filter, ct).ConfigureAwait(false);

        return Results.Ok(new ImportPage(items, total, Math.Max(1, page), size));
    }

    private static async Task<IResult> GetAsync(
        long jobId, IImportJobRepository repository, CancellationToken ct)
    {
        var detail = await repository.GetJobAsync(jobId, ct).ConfigureAwait(false);
        return detail is null ? Results.NotFound() : Results.Ok(detail);
    }

    /// <summary>
    /// Reads the first few lines of a stored file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reads exactly as many lines as it needs and stops. These files reach a gigabyte, so a
    /// preview that opened the whole thing would be a way to take the server down by clicking a
    /// link twenty times.
    /// </para>
    /// <para>
    /// The rows are returned raw, unparsed and unformatted. The point of a preview is to see what
    /// the source actually sent - a stray quote, a shifted column, a header that changed - and
    /// any tidying the API did on the way past would hide precisely the thing being looked for.
    /// </para>
    /// </remarks>
    private static async Task<IResult> PreviewAsync(
        long jobId,
        IImportJobRepository repository,
        IImportFileStore fileStore,
        CancellationToken ct,
        int lines = 20)
    {
        var detail = await repository.GetJobAsync(jobId, ct).ConfigureAwait(false);

        if (detail is null)
        {
            return Results.NotFound();
        }

        if (!detail.IsBlobPresent
            || !await fileStore.ExistsAsync(detail.StoredPath, ct).ConfigureAwait(false))
        {
            return Results.Problem(
                title: "Original file is gone",
                detail: "The stored file has been deleted, so it cannot be previewed.",
                statusCode: StatusCodes.Status410Gone);
        }

        var take = Math.Clamp(lines, 1, 200);

        await using var stream = await fileStore
            .OpenReadAsync(detail.StoredPath, ct).ConfigureAwait(false);
        using var reader = new StreamReader(
            stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true,
            bufferSize: 64 * 1024);

        var header = await reader.ReadLineAsync(ct).ConfigureAwait(false);
        var rows = new List<string>(take);

        while (rows.Count < take
               && await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            rows.Add(line.Length > 1000 ? line[..1000] : line);
        }

        return Results.Ok(new FilePreview(
            detail.Summary.OriginalFileName,
            detail.Summary.FileBytes,
            header?.Split(',').Select(c => c.Trim().Trim('"')).ToArray() ?? [],
            rows,
            detail.Summary.RowsInput));
    }

    private static async Task<IResult> GetQuarantineSamplesAsync(
        long jobId, long summaryId, IImportJobRepository repository, CancellationToken ct)
    {
        var samples = await repository
            .GetQuarantineSamplesAsync(summaryId, 20, ct).ConfigureAwait(false);
        return Results.Ok(samples);
    }

    private static async Task<IResult> CancelAsync(
        long jobId, IImportJobRepository repository, HttpContext http, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);

        var result = await repository
            .RequestCancellationAsync(jobId, CurrentActor(http), ct).ConfigureAwait(false);

        return result switch
        {
            CancelRequestResult.Accepted => Results.Accepted($"/api/v1/imports/{jobId}"),
            CancelRequestResult.NotFound => Results.NotFound(),
            CancelRequestResult.PastPointOfNoReturn => Results.Problem(
                title: "Too late to cancel",
                detail: "The import has loaded its data and is folding it into the history and the "
                        + "dashboards. Stopping now would leave them half-updated, so it will finish; "
                        + "the file can be imported again afterwards.",
                statusCode: StatusCodes.Status409Conflict),
            _ => Results.Problem(
                title: "Cannot cancel",
                detail: "The import has already finished.",
                statusCode: StatusCodes.Status409Conflict),
        };
    }

    private static async Task<IResult> ReprocessAsync(
        long jobId, IImportJobRepository repository, IImportFileStore fileStore, HttpContext http, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);

        // The rules are ImportIntake's: --reprocess follows the same ones from the shell.
        var result = await ImportIntake
            .ReprocessAsync(repository, fileStore, jobId, CurrentActor(http), http.TraceIdentifier, ct)
            .ConfigureAwait(false);

        return result.Outcome switch
        {
            ReprocessOutcome.NotFound => Results.NotFound(),
            ReprocessOutcome.FileGone => Results.Problem(
                title: "Original file is gone",
                detail: "The stored file has been deleted or is not where it was recorded, so this import cannot be run again.",
                statusCode: StatusCodes.Status409Conflict),
            _ => Results.Created($"/api/v1/imports/{result.QueuedJobId}", new ReprocessResponse(result.QueuedJobId!.Value, jobId)),
        };
    }

    private static async Task<IResult> GetFreshnessAsync(
        IImportJobRepository repository, TimeProvider clock, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(clock);

        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var freshness = await repository.GetFreshnessAsync(today, ct).ConfigureAwait(false);
        return Results.Ok(freshness);
    }

    private static async Task<IResult> GetBlockagesAsync(IImportJobRepository repository, CancellationToken ct) =>
        Results.Ok(await repository.GetBlockagesAsync(ct).ConfigureAwait(false));

    private static async Task<IResult> GetUnqueuedFilesAsync(
        IImportJobRepository repository, TimeProvider clock, CancellationToken ct)
    {
        var waiting = await ImportIntake.ListWaitingAsync(repository, Today(clock), ct).ConfigureAwait(false);
        return Results.Ok(waiting.Select(UnqueuedFileResponse.Of));
    }

    private static async Task<IResult> QueueUnqueuedFileAsync(
        string sourceCode,
        long fileId,
        IImportJobRepository repository,
        IImportFileStore fileStore,
        IAuditLog audit,
        TimeProvider clock,
        HttpContext http,
        CancellationToken ct,
        [FromBody] QueueFileRequest? request = null)
    {
        ArgumentNullException.ThrowIfNull(http);

        var refusal = await RefuseUploadAsync(sourceCode, http, audit, ct).ConfigureAwait(false);
        if (refusal is not null)
        {
            return refusal;
        }

        DateOnly? said = null;
        if (!string.IsNullOrWhiteSpace(request?.Date))
        {
            if (ParseDate(request.Date) is not { } parsed)
            {
                return Results.Problem(
                    title: "Not a date",
                    detail: "Write the date as YYYY-MM-DD.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            said = parsed;
        }

        var result = await ImportIntake
            .QueueStoredFileAsync(
                repository, fileStore, sourceCode.ToUpperInvariant(), fileId, said, CurrentActor(http),
                http.TraceIdentifier, Today(clock), ct)
            .ConfigureAwait(false);

        return result.Outcome switch
        {
            ReprocessOutcome.NotFound => Results.NotFound(),
            ReprocessOutcome.FileGone => Results.Problem(
                title: "The stored file cannot be read",
                detail: "It is not where it was recorded: the disk holding the files may be away, or the file was deleted.",
                statusCode: StatusCodes.Status409Conflict),
            ReprocessOutcome.DateNeeded => Results.Problem(
                title: "This file needs its date",
                detail: result.Problem,
                statusCode: StatusCodes.Status400BadRequest),
            ReprocessOutcome.DateRefused => Results.Problem(
                title: "That date cannot be this file's",
                detail: result.Problem,
                statusCode: StatusCodes.Status400BadRequest),
            _ => Results.Created($"/api/v1/imports/{result.QueuedJobId}", new QueuedFileResponse(result.QueuedJobId!.Value, fileId)),
        };
    }

    private static DateOnly Today(TimeProvider clock) => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

    private static async Task<IResult> GetStageDurationsAsync(
        string source, IImportJobRepository repository, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(source)
            ? Results.Problem(title: "Missing source", detail: "Name the source: ?source=SQM.",
                statusCode: StatusCodes.Status400BadRequest)
            : Results.Ok(await repository.GetStageDurationsAsync(source, ct).ConfigureAwait(false));

    private static async Task<IResult> GetWorkerHealthAsync(
        IImportJobRepository repository, IImportFileStore fileStore, CancellationToken ct)
    {
        var health = await repository.GetWorkerHealthAsync(ct).ConfigureAwait(false);
        var reachable = await fileStore.IsReachableAsync(ct).ConfigureAwait(false);
        return Results.Ok(health with { StoreReachable = reachable });
    }

    /// <summary>
    /// Parses a comma-separated status filter. Returns <see langword="null"/> when a value is not
    /// a status, so the caller can answer 400 rather than silently ignoring it.
    /// </summary>
    private static List<ImportJobStatus>? ParseStatuses(string? value)
    {
        var statuses = new List<ImportJobStatus>();

        if (string.IsNullOrWhiteSpace(value))
        {
            return statuses;
        }

        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Enum.TryParse<ImportJobStatus>(part.Replace("_", "", StringComparison.Ordinal), true, out var status))
            {
                return null;
            }

            statuses.Add(status);
        }

        return statuses;
    }

    private static DateOnly? ParseDate(string? value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date) ? date : null;

    /// <summary>
    /// Who is performing this action.
    /// </summary>
    /// <remarks>
    /// Authentication is not wired up yet, so this reads the authenticated name when there is one
    /// and falls back to a marker that is obviously a placeholder rather than a plausible user
    /// name. Audit rows written before authentication exists should say so.
    /// </remarks>
    /// <summary>
    /// Everything that can refuse an upload without looking at the bytes.
    /// </summary>
    /// <param name="sourceCode">The source the file is for.</param>
    /// <param name="http">The request, for the caller and their address.</param>
    /// <param name="audit">Where a refusal is recorded.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The problem to return, or <see langword="null"/> when it would be accepted.</returns>
    /// <remarks>
    /// <para>
    /// <b>One function, two callers, on purpose.</b> The preflight exists to give the same answer
    /// the upload would give, and the fastest way to make it lie is to write the rule twice: a
    /// preflight that says yes where the upload says no wastes the very upload it was added to
    /// prevent, and one that says no where the upload says yes silently removes a capability.
    /// </para>
    /// <para>
    /// Which permission applies depends on the source, so it cannot be declared on the route.
    /// </para>
    /// </remarks>
    internal static async Task<IResult?> RefuseUploadAsync(
        string sourceCode, HttpContext http, IAuditLog audit, CancellationToken ct)
    {
        var source = sourceCode.ToUpperInvariant();

        var required = source switch
        {
            "SQM" => Permissions.ImportUploadSqm,
            "TAC" => Permissions.ImportUploadTac,
            _ => null,
        };

        if (required is null)
        {
            return Results.Problem(
                title: "Unknown source",
                detail: $"'{sourceCode}' is not a source this platform accepts.",
                statusCode: StatusCodes.Status404NotFound);
        }

        var user = CurrentUser.Require(http);

        if (user.Can(required))
        {
            return null;
        }

        // Audited here, because a check made inside a handler never reaches the pipeline's
        // authorisation result handler - so without this the one refusal that matters most in
        // the import platform would be the only one leaving no trace.
        //
        // Both callers reach this, and that is not double-counting: a caller refused by the
        // preflight does not go on to send the body, so a refused upload still produces one
        // entry. A caller allowed here is audited by the upload itself when it succeeds.
        await audit.WriteAsync(new Sqm.Application.Identity.AuditEntry(
            ActorName: user.Username,
            Action: required,
            Category: Sqm.Application.Identity.AuditCategory.Import,
            Outcome: Sqm.Application.Identity.AuditOutcome.Denied,
            ActorUserId: user.UserId,
            TargetType: "source",
            TargetId: source,
            Ip: http.Connection.RemoteIpAddress?.ToString(),
            CorrelationId: http.TraceIdentifier), ct).ConfigureAwait(false);

        return Results.Problem(
            title: "Not permitted",
            detail: $"Uploading {source} files requires the '{required}' permission.",
            statusCode: StatusCodes.Status403Forbidden);
    }

    /// <summary>Answers whether a file would be accepted, before it is sent.</summary>
    /// <remarks>
    /// <para>
    /// <b>What this cannot tell you, and does not pretend to.</b> The one remaining refusal is a
    /// duplicate, decided by the SHA-256 of the content - and the only way to know that is to
    /// have the content. Guessing from the file name and size would be worse than useless: a
    /// corrected re-delivery usually arrives under the same name at nearly the same size, and
    /// refusing it would be refusing exactly the file an operator most needs to send.
    /// </para>
    /// <para>
    /// So the duplicate check stays where the bytes are, and stays a 409 rather than an error -
    /// re-sending a file is a normal thing for an operator to do.
    /// </para>
    /// </remarks>
    private static async Task<IResult> PreflightAsync(
        string sourceCode,
        UploadPreflightRequest request,
        HttpContext http,
        IAuditLog audit,
        TimeProvider clock,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var refusal = await RefuseUploadAsync(sourceCode, http, audit, ct).ConfigureAwait(false);
        if (refusal is not null)
        {
            return refusal;
        }

        // What the name says of the date, said before the bytes are sent: over the VPN a day is
        // forty minutes of upload, and "you will be asked for its date" is better known at the
        // start. An archive's own name settles nothing yet - what it holds is not known until it
        // is here - so nothing is said of it.
        var name = request.FileName ?? string.Empty;
        var packed = name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".rar", StringComparison.OrdinalIgnoreCase);
        var date = packed
            ? null
            : FileDate.Decide(
                name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? name[..^3] : name,
                null, onlyFileInArchive: false, Today(clock));

        return Results.Ok(new UploadPreflightResponse(
            Source: sourceCode.ToUpperInvariant(),
            FileName: name,
            SizeBytes: request.SizeBytes,
            Message: "Accepted. Content is checked for duplicates once the file arrives.",
            DateState: date is null ? null : DateStateText(date),
            Date: date is null ? null : DateText(date),
            DateCandidates: date is null ? [] : [.. date.Candidates.Select(FileDate.Text)]));
    }

    internal static string CurrentActor(HttpContext http) =>
        http.User.Identity?.Name is { Length: > 0 } name ? name : "anonymous@pre-auth";
}

/// <summary>What the client is about to send.</summary>
/// <param name="FileName">The name the file will arrive under.</param>
/// <param name="SizeBytes">How large it is, so the answer can name it.</param>
/// <remarks>
/// Neither field decides anything today - the refusals that can be made without the body depend
/// on the source and the caller, not on the file. They are carried so the answer is about a
/// named file, and so a future size or naming rule has somewhere to live.
/// </remarks>
public sealed record UploadPreflightRequest(string FileName, long SizeBytes);

/// <summary>The upload would be accepted.</summary>
/// <param name="Source">The source, normalised.</param>
/// <param name="FileName">The file the answer is about.</param>
/// <param name="SizeBytes">Its size as the client reported it.</param>
/// <param name="Message">What is still undecided, said plainly.</param>
/// <param name="DateState">
/// What the name says of the file's date: settled, ambiguous, several, missing or future. Null for
/// an archive, whose files are not known until it arrives.
/// </param>
/// <param name="Date">The date, when the name settles it.</param>
/// <param name="DateCandidates">What the name can mean, when it does not settle it.</param>
public sealed record UploadPreflightResponse(
    string Source,
    string FileName,
    long SizeBytes,
    string Message,
    string? DateState = null,
    string? Date = null,
    IReadOnlyList<string>? DateCandidates = null);

/// <summary>The first rows of a stored file, as delivered.</summary>
/// <param name="FileName">The name it arrived under.</param>
/// <param name="FileBytes">Its size.</param>
/// <param name="Columns">The header, split on commas.</param>
/// <param name="Rows">The first data lines, raw.</param>
/// <param name="TotalRows">Rows the import counted, or 0 if it has not run yet.</param>
public sealed record FilePreview(
    string FileName, long FileBytes, IReadOnlyList<string> Columns,
    IReadOnlyList<string> Rows, long TotalRows);

/// <summary>Returned when an upload is accepted and queued.</summary>
/// <param name="JobId">The queued job.</param>
/// <param name="FileId">The stored file.</param>
/// <param name="FileName">The name it is stored under: a gzip's, without <c>.gz</c>.</param>
/// <param name="SizeBytes">Bytes stored - for a gzip, what it unpacked to.</param>
/// <param name="Sha256">Hash of what is stored.</param>
/// <param name="Date">The date the file was given, YYYY-MM-DD.</param>
/// <param name="DateFrom">Who gave it: name, archive, person or recorded.</param>
public sealed record UploadAcceptedResponse(
    long JobId, long FileId, string FileName, long SizeBytes, string Sha256,
    string? Date = null, string? DateFrom = null);

/// <summary>Returned when a file is stored and waits for a person to give its date.</summary>
/// <param name="FileId">The stored file, to queue once its date is known.</param>
/// <param name="FileName">The name it is stored under.</param>
/// <param name="SizeBytes">Bytes stored.</param>
/// <param name="Sha256">Hash of what is stored.</param>
/// <param name="DateState">Why it waits: ambiguous, several, missing or future.</param>
/// <param name="DateCandidates">What may be chosen; empty when any day that has happened may be given.</param>
/// <param name="Message">The same, in words.</param>
public sealed record UploadWaitingResponse(
    long FileId, string FileName, long SizeBytes, string Sha256,
    string DateState, IReadOnlyList<string> DateCandidates, string Message);

/// <summary>Returned for an archive: what became of each file in it.</summary>
/// <param name="Archive">The name the archive arrived under.</param>
/// <param name="Kind">zip or rar, told from its first bytes.</param>
/// <param name="Files">The CSV files taken from it.</param>
/// <param name="Skipped">What it held besides, and why each was left.</param>
public sealed record ArchiveUploadResponse(
    string Archive, string Kind, IReadOnlyList<ArchivedFileResponse> Files, IReadOnlyList<SkippedEntryResponse> Skipped);

/// <summary>One file taken from an archive.</summary>
/// <param name="FileName">Its own name.</param>
/// <param name="FileId">The stored file.</param>
/// <param name="SizeBytes">Bytes stored.</param>
/// <param name="Sha256">Hash of what is stored.</param>
/// <param name="Outcome">queued, duplicate or waiting.</param>
/// <param name="JobId">Its job, when queued.</param>
/// <param name="ExistingJobId">The import its content already had, when a duplicate.</param>
/// <param name="Date">Its date, when settled.</param>
/// <param name="DateFrom">Who settled it: name, archive, person or recorded.</param>
/// <param name="DateState">settled, or why it waits: ambiguous, several, missing or future.</param>
/// <param name="DateCandidates">What may be chosen when it waits.</param>
/// <param name="Message">What happened, in words.</param>
public sealed record ArchivedFileResponse(
    string FileName, long FileId, long SizeBytes, string Sha256, string Outcome, long? JobId, long? ExistingJobId,
    string? Date, string? DateFrom, string DateState, IReadOnlyList<string> DateCandidates, string Message)
{
    internal static ArchivedFileResponse Of(ArrivedOutcome file)
    {
        var intake = file.Intake;
        var date = ImportEndpoints.DateText(intake.Date);

        var (outcome, message) = intake.Outcome switch
        {
            IntakeOutcome.Queued => ("queued", $"Queued as import {intake.QueuedJobId}, dated {date}."),
            IntakeOutcome.AlreadyImported => (
                "duplicate",
                intake.File.ExistingJobId is { } existing
                    ? $"Already imported: see import {existing}."
                    : "This exact file has already been uploaded."),
            _ => ("waiting", intake.DateProblem ?? intake.Date.Explain(file.FileName)),
        };

        return new ArchivedFileResponse(
            file.FileName, intake.File.FileId, file.Stored.SizeBytes, file.Stored.Sha256, outcome,
            intake.QueuedJobId, intake.File.ExistingJobId, date, ImportEndpoints.DateSourceText(intake.Date),
            ImportEndpoints.DateStateText(intake.Date), [.. intake.Date.Candidates.Select(FileDate.Text)], message);
    }
}

/// <summary>Something an archive held that was not taken.</summary>
/// <param name="Name">Its path inside the archive.</param>
/// <param name="Reason">Why it was left.</param>
public sealed record SkippedEntryResponse(string Name, string Reason);

/// <summary>A stored file with no import, as the Import Center lists it.</summary>
/// <param name="FileId">The file.</param>
/// <param name="SourceCode">Its source.</param>
/// <param name="OriginalFileName">The name it is stored under.</param>
/// <param name="ArchiveName">The archive it was unpacked from, if it was.</param>
/// <param name="SizeBytes">Its size.</param>
/// <param name="UploadedBy">Who sent it.</param>
/// <param name="UploadedAt">When it was registered.</param>
/// <param name="DateState">settled: it only needs queueing. Otherwise why it waits.</param>
/// <param name="Date">Its date, when settled.</param>
/// <param name="DateCandidates">What may be chosen; empty when any day that has happened may be given.</param>
/// <param name="Message">The same, in words.</param>
public sealed record UnqueuedFileResponse(
    long FileId, string SourceCode, string OriginalFileName, string? ArchiveName, long SizeBytes,
    string UploadedBy, DateTimeOffset UploadedAt, string DateState, string? Date,
    IReadOnlyList<string> DateCandidates, string Message)
{
    internal static UnqueuedFileResponse Of(WaitingFile waiting)
    {
        var (file, date) = (waiting.File, waiting.Date);

        return new UnqueuedFileResponse(
            file.FileId, file.SourceCode, file.OriginalFileName, file.ArchiveName, file.SizeBytes, file.UploadedBy,
            file.UploadedAt, ImportEndpoints.DateStateText(date), ImportEndpoints.DateText(date),
            [.. date.Candidates.Select(FileDate.Text)], date.Explain(file.OriginalFileName));
    }
}

/// <summary>What a person says when queueing a stored file.</summary>
/// <param name="Date">Its date, YYYY-MM-DD, where its name does not settle one.</param>
public sealed record QueueFileRequest(string? Date);

/// <summary>Returned when the uploaded content was already known.</summary>
/// <param name="FileId">The file already on record.</param>
/// <param name="ExistingJobId">Its most recent import job.</param>
/// <param name="OriginalFileName">The name it arrived under the first time.</param>
/// <param name="Sha256">Content hash.</param>
/// <param name="Message">What happened, in plain words.</param>
public sealed record DuplicateUploadResponse(
    long FileId, long? ExistingJobId, string OriginalFileName, string Sha256, string Message);

/// <summary>Returned when a file is queued again.</summary>
/// <param name="JobId">The new job.</param>
/// <param name="ReprocessOfJobId">The job it re-runs.</param>
public sealed record ReprocessResponse(long JobId, long ReprocessOfJobId);

/// <summary>A stored file with no job, now queued.</summary>
/// <param name="JobId">The job it was given.</param>
/// <param name="FileId">The file.</param>
public sealed record QueuedFileResponse(long JobId, long FileId);
