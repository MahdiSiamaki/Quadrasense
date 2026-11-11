namespace Sqm.Application.DataImport;

/// <summary>A file whose bytes are stored and which is about to be registered.</summary>
/// <param name="SourceCode">Data source, upper case.</param>
/// <param name="OriginalFileName">The name the file arrived under; its date is read from it.</param>
/// <param name="Stored">Where the bytes went, and their hash.</param>
/// <param name="ArchiveName">The archive it was unpacked from, if it was.</param>
/// <param name="OnlyFileInArchive">Whether it is the one file that archive held: then the archive's name may date it.</param>
/// <param name="SaidDate">The date whoever handed the file over gave for it, if they gave one.</param>
public sealed record ArrivedFile(
    string SourceCode,
    string OriginalFileName,
    StoredFile Stored,
    string? ArchiveName = null,
    bool OnlyFileInArchive = false,
    DateOnly? SaidDate = null);

/// <summary>What became of a file that arrived.</summary>
public enum IntakeOutcome
{
    /// <summary>Registered and queued for import.</summary>
    Queued,

    /// <summary>Its content already has an import; nothing was queued.</summary>
    AlreadyImported,

    /// <summary>Stored and registered, and waiting for a person to give its date.</summary>
    WaitingForDate,
}

/// <summary>
/// What becomes of a file once its bytes are stored: a new import in the queue, the import its
/// content already has, or a wait for its date.
/// </summary>
/// <remarks>
/// <para>
/// One place for the rules, because a file arrives several ways: the Import Center's upload, a
/// resumable upload, <c>--import-file</c> for a file already on the server, and any of them packed
/// in an archive. The second exists because uploads through the department's VPN broke at every
/// drop (2026-10-25: two of two attempts at a 370 MB day, at 12.6 and 16.5 minutes). Two copies of
/// these rules would disagree the first time one of them was corrected.
/// </para>
/// <para>
/// The caller stores the bytes first - <see cref="IImportFileStore.SaveAsync"/> hashes them as they
/// are written - and decides who may do this: the endpoint checks the user's permission, the
/// command is run by whoever holds the server.
/// </para>
/// </remarks>
public static class ImportIntake
{
    /// <summary>
    /// Registers a stored file and queues its import, finds the import it already has, or leaves
    /// it waiting for its date.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>No cancellation token, and that is the point.</b> The bytes are stored by the time this
    /// runs, so there is nothing left to save by stopping: stopping part way can only leave a file
    /// registered with no job. Only the enqueue used to be off the request's token. On 2026-11-03
    /// the file for 26 October was registered and got neither a job nor an audit row, among
    /// neighbours whose steps took up to 10.8 seconds apart while an import was running: a long
    /// window for a connection to end in. Nothing logged why that one stopped, so the token is a
    /// likely cause, not a proven one; a failed enqueue would look the same. Whatever the cause,
    /// a file that does end up here is listed for the operator: see
    /// <see cref="QueueStoredFileAsync"/>.
    /// </para>
    /// <para>
    /// <b>The date is decided here, once</b> (<see cref="FileDate"/>), and recorded on the file
    /// with who gave it. A daily file's job carries it too, which is what puts the job in the right
    /// place in the queue: the claim refuses to run a day while an earlier day of the source is
    /// still unlanded, and it can only compare dates that jobs already carry. A file whose names do
    /// not settle its date gets no job: a daily file filed under the wrong day is believed by
    /// everything after it, so it waits for a person rather than for a guess.
    /// </para>
    /// </remarks>
    /// <param name="repository">The Import Center's history and queue.</param>
    /// <param name="files">The file store the bytes were written to.</param>
    /// <param name="arrived">The file, and what is known of where it came from.</param>
    /// <param name="actor">Who sent it.</param>
    /// <param name="correlationId">The request's trace identifier, when there is a request.</param>
    /// <param name="today">Today, in UTC: a date later than tomorrow is not believed.</param>
    public static async Task<IntakeResult> AcceptAsync(
        IImportJobRepository repository,
        IImportFileStore files,
        ArrivedFile arrived,
        string actor,
        string? correlationId,
        DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(arrived);

        var (sourceCode, originalFileName, stored) = (arrived.SourceCode, arrived.OriginalFileName, arrived.Stored);

        var date = FileDate.Decide(originalFileName, arrived.ArchiveName, arrived.OnlyFileInArchive, today);
        string? dateProblem = null;

        if (arrived.SaidDate is { } said)
        {
            date = FileDate.Accept(date, said, today, out dateProblem) ?? date;
        }

        // A date that was given and not taken stops the file here, even where its name says one:
        // whoever gave it meant something, and queueing under the name's date instead would be
        // answering a question they did not ask. It is stored, and can be queued from the list.
        var goesOn = date.IsSettled && dateProblem is null;

        var registered = await repository
            .RegisterFileAsync(
                sourceCode, originalFileName, stored, actor, arrived.ArchiveName, date.IsSettled ? date : null,
                CancellationToken.None)
            .ConfigureAwait(false);

        if (!registered.IsNew)
        {
            // The bytes just written duplicate a file already on disk. Removing the copy keeps
            // storage honest: one content hash, one stored blob.
            await files.DeleteAsync(stored.StoredPath, CancellationToken.None).ConfigureAwait(false);
        }

        // A known file with no job is not a duplicate import, it is an unfinished one: the file
        // was registered and the request ended before its job was queued, or it has been waiting
        // for its date. Refusing it as a duplicate left that content importable by no route at
        // all, since reprocessing needs a job to start from. It goes on now, from the copy already
        // stored.
        if (!registered.IsNew && registered.ExistingJobId is not null)
        {
            await repository.WriteAuditAsync(
                actor, "import.upload.duplicate", registered.ExistingJobId, registered.FileId,
                null, correlationId,
                new { fileName = originalFileName, sha256 = stored.Sha256, archive = arrived.ArchiveName },
                CancellationToken.None).ConfigureAwait(false);

            return new IntakeResult(registered, IntakeOutcome.AlreadyImported, null, date, dateProblem);
        }

        if (!registered.IsNew)
        {
            if (date.IsSettled)
            {
                // This arrival says what the earlier one could not, or says it again.
                await repository
                    .SetFileDateAsync(registered.FileId, date, arrived.ArchiveName, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            else if (dateProblem is null && registered.ExistingDeclaredDate is { } earlier)
            {
                // The earlier copy came under a name that said its date; this one's does not.
                date = FileDateDecision.Recorded(earlier);
                goesOn = true;
            }
        }

        if (!goesOn)
        {
            await repository.WriteAuditAsync(
                actor, "import.upload.undated", null, registered.FileId, null, correlationId,
                new
                {
                    fileName = originalFileName,
                    bytes = stored.SizeBytes,
                    sha256 = stored.Sha256,
                    archive = arrived.ArchiveName,
                    why = dateProblem ?? date.State.ToString(),
                },
                CancellationToken.None).ConfigureAwait(false);

            return new IntakeResult(registered, IntakeOutcome.WaitingForDate, null, date, dateProblem);
        }

        var jobId = await EnqueueDatedAsync(
            repository, sourceCode, registered.FileId, date, arrived.ArchiveName, actor).ConfigureAwait(false);

        await repository.WriteAuditAsync(
            actor, "import.upload", jobId, registered.FileId, null, correlationId,
            new
            {
                fileName = originalFileName,
                bytes = stored.SizeBytes,
                sha256 = stored.Sha256,
                archive = arrived.ArchiveName,
                date = FileDate.Text(date.Date!.Value),
                dateFrom = date.Source?.ToString(),
            },
            CancellationToken.None).ConfigureAwait(false);

        return new IntakeResult(registered, IntakeOutcome.Queued, jobId, date);
    }

    /// <summary>Stored files with no job, each with what is known about its date.</summary>
    /// <remarks>
    /// A file with a recorded date, or a name that says one, only needs queueing. The rest wait
    /// for a person, and are told what the name can mean. The archive a file came in is not asked
    /// again here: where it could date the file it did so when the file arrived, and what its name
    /// offered beyond that is not kept - the person is then asked for the date outright.
    /// </remarks>
    public static async Task<IReadOnlyList<WaitingFile>> ListWaitingAsync(
        IImportJobRepository repository, DateOnly today, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(repository);

        var unqueued = await repository.GetUnqueuedFilesAsync(ct).ConfigureAwait(false);
        return [.. unqueued.Select(file => new WaitingFile(file, DateOf(file, today)))];
    }

    /// <summary>Queues a stored file that has no job, from the copy already stored.</summary>
    /// <remarks>
    /// <para>
    /// The way on for a file that waits for its date - <paramref name="saidDate"/> is the person's
    /// answer - and the way back for one <see cref="AcceptAsync"/> did not finish with: a failed
    /// enqueue, a process that died. A day with no job blocks nothing, so nothing else would ever
    /// say it is missing, and this list is what does. Sending the file again also works, and costs
    /// the whole upload. Only a file still on the list is queued, so two people asking cannot queue
    /// it twice unless they ask in the same instant, and then the day is imported twice, which is
    /// harmless.
    /// </para>
    /// <para>
    /// A file gone from the store answers <see cref="ReprocessOutcome.FileGone"/>: with the store
    /// away a job queued now would only wait, and saying so is more use than a queue entry.
    /// </para>
    /// </remarks>
    /// <param name="repository">The Import Center's history and queue.</param>
    /// <param name="files">The file store the original is read from.</param>
    /// <param name="sourceCode">Data source, upper case. The file must belong to it.</param>
    /// <param name="fileId">The file to queue.</param>
    /// <param name="saidDate">The date a person gives it; needed when its name does not settle one.</param>
    /// <param name="actor">Who asked for it.</param>
    /// <param name="correlationId">The request's trace identifier, when there is a request.</param>
    /// <param name="today">Today, in UTC.</param>
    /// <param name="ct">Cancellation token; it stops the checks, never the queueing.</param>
    public static async Task<ReprocessResult> QueueStoredFileAsync(
        IImportJobRepository repository,
        IImportFileStore files,
        string sourceCode,
        long fileId,
        DateOnly? saidDate,
        string actor,
        string? correlationId,
        DateOnly today,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(files);

        var unqueued = await repository.GetUnqueuedFilesAsync(ct).ConfigureAwait(false);
        var file = unqueued.FirstOrDefault(f => f.FileId == fileId && f.SourceCode == sourceCode);

        if (file is null)
        {
            return new ReprocessResult(ReprocessOutcome.NotFound, null, null);
        }

        var date = DateOf(file, today);

        if (saidDate is { } said)
        {
            if (FileDate.Accept(date, said, today, out var problem) is not { } accepted)
            {
                return new ReprocessResult(ReprocessOutcome.DateRefused, null, file.StoredPath, problem);
            }

            date = accepted;
        }

        if (!date.IsSettled)
        {
            return new ReprocessResult(
                ReprocessOutcome.DateNeeded, null, file.StoredPath, date.Explain(file.OriginalFileName));
        }

        if (!await files.ExistsAsync(file.StoredPath, ct).ConfigureAwait(false))
        {
            return new ReprocessResult(ReprocessOutcome.FileGone, null, file.StoredPath);
        }

        if (date.Source is not null && file.DeclaredDate != date.Date)
        {
            await repository.SetFileDateAsync(file.FileId, date, null, CancellationToken.None).ConfigureAwait(false);
        }

        var jobId = await EnqueueDatedAsync(
            repository, file.SourceCode, file.FileId, date, file.ArchiveName, actor).ConfigureAwait(false);

        await repository.WriteAuditAsync(
            actor, "import.upload.queued", jobId, file.FileId, null, correlationId,
            new
            {
                fileName = file.OriginalFileName,
                bytes = file.SizeBytes,
                date = FileDate.Text(date.Date!.Value),
                dateFrom = date.Source?.ToString() ?? "Recorded",
            },
            CancellationToken.None).ConfigureAwait(false);

        return new ReprocessResult(ReprocessOutcome.Queued, jobId, file.StoredPath);
    }

    /// <summary>"Import again": queues a finished import's file as a new job, its day's next revision.</summary>
    /// <remarks>
    /// A new job against the same file, not a reset of the old one. History is a record of what
    /// happened, and rewriting the failed attempt would erase the evidence of why the reprocess was
    /// needed. The file must be readable, not only recorded as present: the first machine's 134
    /// bulk-loaded files are recorded at a drive they have since left (2026-10-26), and a job
    /// queued against one could only fail.
    /// </remarks>
    /// <param name="repository">The Import Center's history and queue.</param>
    /// <param name="files">The file store the original is read from.</param>
    /// <param name="jobId">The import to run again.</param>
    /// <param name="actor">Who asked for it.</param>
    /// <param name="correlationId">The request's trace identifier, when there is a request.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<ReprocessResult> ReprocessAsync(
        IImportJobRepository repository,
        IImportFileStore files,
        long jobId,
        string actor,
        string? correlationId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(files);

        var original = await repository.GetJobAsync(jobId, ct).ConfigureAwait(false);
        if (original is null)
        {
            return new ReprocessResult(ReprocessOutcome.NotFound, null, null);
        }

        if (!original.IsBlobPresent || !await files.ExistsAsync(original.StoredPath, ct).ConfigureAwait(false))
        {
            return new ReprocessResult(ReprocessOutcome.FileGone, null, original.StoredPath);
        }

        var newJobId = await repository.EnqueueAsync(
            original.Summary.SourceCode,
            original.FileId,
            original.Summary.BusinessDate,
            actor,
            priority: 1,
            reprocessOfJobId: jobId,
            ct).ConfigureAwait(false);

        await repository.WriteAuditAsync(
            actor, "import.reprocess", newJobId, null, null, correlationId,
            new { originalJobId = jobId }, ct).ConfigureAwait(false);

        return new ReprocessResult(ReprocessOutcome.Queued, newJobId, original.StoredPath);
    }

    /// <summary>What is known of a stored file's date: what was recorded, else what its name says.</summary>
    private static FileDateDecision DateOf(UnqueuedFile file, DateOnly today) =>
        file.DeclaredDate is { } recorded
            ? FileDateDecision.Recorded(recorded)
            : FileDate.Decide(file.OriginalFileName, null, onlyFileInArchive: false, today);

    /// <summary>
    /// Queues a file whose date is settled, and writes on its timeline what the date is and who
    /// said so - so that a date read from a name can be seen, and seen to be wrong.
    /// </summary>
    private static async Task<long> EnqueueDatedAsync(
        IImportJobRepository repository,
        string sourceCode,
        long fileId,
        FileDateDecision date,
        string? archiveName,
        string actor)
    {
        var day = date.Date!.Value;

        var jobId = await repository
            .EnqueueAsync(
                sourceCode, fileId, ImportSources.JobCarriesTheDate(sourceCode) ? day : null, actor,
                ct: CancellationToken.None)
            .ConfigureAwait(false);

        var said = date.Source switch
        {
            FileDateSource.Name => "from its name",
            FileDateSource.Archive => $"from the name of the archive it arrived in, {archiveName}",
            FileDateSource.Person => $"by {actor}: its name did not settle it",
            _ => "as recorded when it arrived",
        };

        await repository.AppendEventAsync(
            jobId, "info", null, $"Dated {FileDate.Text(day)}, {said}.",
            new { date = FileDate.Text(day), from = date.Source?.ToString() ?? "Recorded", archive = archiveName },
            CancellationToken.None).ConfigureAwait(false);

        return jobId;
    }
}

/// <summary>What became of a request to queue a stored file, or to import a job's file again.</summary>
public enum ReprocessOutcome
{
    /// <summary>Queued as a new job.</summary>
    Queued,

    /// <summary>There is no such import, or no such waiting file.</summary>
    NotFound,

    /// <summary>Its original file cannot be read: deleted, or not where it is recorded.</summary>
    FileGone,

    /// <summary>The file's name does not settle its date, and none was given.</summary>
    DateNeeded,

    /// <summary>The date given cannot be the file's: its name says otherwise, or it has not happened.</summary>
    DateRefused,
}

/// <summary>The outcome of <see cref="ImportIntake.ReprocessAsync"/> and <see cref="ImportIntake.QueueStoredFileAsync"/>.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="QueuedJobId">The new job, when one was queued.</param>
/// <param name="StoredPath">Where the original was looked for.</param>
/// <param name="Problem">Why a date was needed or refused, in words for the person asked.</param>
public sealed record ReprocessResult(
    ReprocessOutcome Outcome, long? QueuedJobId, string? StoredPath, string? Problem = null);

/// <summary>The outcome of <see cref="ImportIntake.AcceptAsync"/>.</summary>
/// <param name="File">The registered file - new, or the one these bytes already were.</param>
/// <param name="Outcome">Queued, already imported, or waiting for its date.</param>
/// <param name="QueuedJobId">The import just queued, when one was.</param>
/// <param name="Date">The file's date, or why it has none and what can be chosen.</param>
/// <param name="DateProblem">Why a date that was given with the file was not taken.</param>
public sealed record IntakeResult(
    RegisteredFile File,
    IntakeOutcome Outcome,
    long? QueuedJobId,
    FileDateDecision Date,
    string? DateProblem = null);
