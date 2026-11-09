using System.Xml.Linq;

namespace Sqm.Integration.Tests;

/// <summary>
/// The backend image is built from an allow-list of what it copies, so a file the projects embed
/// from elsewhere in the repository has to be named in its Dockerfile too.
/// </summary>
/// <remarks>
/// The appearance contract (ADR-023) was embedded from <c>frontend/</c> and not copied. Every test
/// passed, because the tests run from a whole checkout, and the department pipeline then failed at
/// the image build with CS1566. Needs no database and no Docker: it reads the files.
/// </remarks>
public sealed class BackendImageTests
{
    [Fact]
    public void Every_file_embedded_from_outside_the_copied_folders_is_copied_into_the_image()
    {
        var root = RepositoryRoot();
        var dockerfile = File.ReadAllText(Path.Combine(root, "deploy", "images", "backend", "Dockerfile"));

        var embedded = Directory
            .EnumerateFiles(Path.Combine(root, "backend", "src"), "*.csproj", SearchOption.AllDirectories)
            .SelectMany(project => XDocument.Load(project)
                .Descendants("EmbeddedResource")
                .Select(item => item.Attribute("Include")?.Value)
                .OfType<string>()
                .Select(include => Path.GetFullPath(Path.Combine(
                    Path.GetDirectoryName(project)!, include.Replace('\\', Path.DirectorySeparatorChar)))))
            .Select(full => Path.GetRelativePath(root, full).Replace('\\', '/'))
            // backend/ and db/ are copied whole.
            .Where(path => !path.StartsWith("backend/", StringComparison.Ordinal)
                && !path.StartsWith("db/", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(embedded);

        foreach (var path in embedded)
        {
            Assert.True(
                dockerfile.Contains($"COPY {path} ", StringComparison.Ordinal),
                $"{path} is embedded by a backend project but not copied by deploy/images/backend/Dockerfile.");
        }
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "global.json")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("No global.json above " + AppContext.BaseDirectory);
    }
}
