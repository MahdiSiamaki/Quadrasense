using System.Reflection;
using Sqm.Application.DataImport;

namespace Sqm.Integration.Tests;

/// <summary>
/// A repository that remembers what it is told, so that the intake's questions have answers.
/// </summary>
/// <remarks>
/// A stub that answers the same thing every time cannot be asked "the same content twice", or "the
/// file that waited, now dated" - and those are the questions the intake exists to answer. This
/// keeps files, jobs, timeline entries and audit rows in memory, by the rules the real repository
/// keeps them by: one file per source and content hash, a job per enqueue, a file with no job
/// listed as waiting. Anything it is not asked to remember it refuses.
/// </remarks>
internal sealed class ImportLedger
{
    public ImportLedger() => Repository = TestStubs.Create<IImportJobRepository>(Handle);

    public IImportJobRepository Repository { get; }

    public List<Row> Files { get; } = [];

    public List<(long JobId, long FileId, string Source, DateOnly? BusinessDate)> Jobs { get; } = [];

    public List<string> Events { get; } = [];

    public List<string> Audits { get; } = [];

    private object? Handle(MethodInfo method, object?[] args)
    {
        switch (method.Name)
        {
            case "RegisterFileAsync":
            {
                var (source, name, stored) = ((string)args[0]!, (string)args[1]!, (StoredFile)args[2]!);
                var known = Files.FirstOrDefault(f => f.Source == source && f.Sha == stored.Sha256);

                if (known is not null)
                {
                    var job = Jobs.Where(j => j.FileId == known.FileId).Select(j => (long?)j.JobId).LastOrDefault();
                    return Task.FromResult(new RegisteredFile(known.FileId, known.Sha, false, job, known.Name, known.Date));
                }

                var date = (FileDateDecision?)args[5];
                var row = new Row(Files.Count + 1, source, name, stored.Sha256, stored.StoredPath, stored.SizeBytes)
                {
                    Archive = (string?)args[4],
                    Date = date?.Date,
                    DateSource = date?.Source,
                };
                Files.Add(row);
                return Task.FromResult(new RegisteredFile(row.FileId, row.Sha, true, null, null));
            }

            case "SetFileDateAsync":
            {
                var row = Files.Single(f => f.FileId == (long)args[0]!);
                var date = (FileDateDecision)args[1]!;
                row.Date = date.Date;
                row.DateSource = date.Source;
                row.Archive ??= (string?)args[2];
                return Task.CompletedTask;
            }

            case "EnqueueAsync":
                Jobs.Add((100 + Jobs.Count, (long)args[1]!, (string)args[0]!, (DateOnly?)args[2]));
                return Task.FromResult(Jobs[^1].JobId);

            case "GetUnqueuedFilesAsync":
                return Task.FromResult<IReadOnlyList<UnqueuedFile>>([.. Files
                    .Where(f => Jobs.All(j => j.FileId != f.FileId))
                    .Select(f => new UnqueuedFile(
                        f.FileId, f.Source, f.Name, f.StoredPath, f.Size, "tester", DateTimeOffset.UtcNow,
                        f.Archive, f.Date))]);

            case "AppendEventAsync":
                Events.Add((string)args[3]!);
                return Task.CompletedTask;

            case "WriteAuditAsync":
                Audits.Add((string)args[1]!);
                return Task.CompletedTask;

            default:
                throw new NotSupportedException(method.Name);
        }
    }

    public sealed class Row(long fileId, string source, string name, string sha, string storedPath, long size)
    {
        public long FileId { get; } = fileId;

        public string Source { get; } = source;

        public string Name { get; } = name;

        public string Sha { get; } = sha;

        public string StoredPath { get; } = storedPath;

        public long Size { get; } = size;

        public string? Archive { get; set; }

        public DateOnly? Date { get; set; }

        public FileDateSource? DateSource { get; set; }
    }
}
