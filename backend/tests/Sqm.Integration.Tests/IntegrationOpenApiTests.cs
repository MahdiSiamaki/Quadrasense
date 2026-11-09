using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Sqm.Integration.Tests;

/// <summary>
/// The integration API's committed OpenAPI document - what Lodestone's and Tessera's developers read - is
/// what the API generates (ADR-024). A route, a field or a scope changed in code and not in the document
/// fails here, naming the first difference.
/// </summary>
/// <remarks>
/// To regenerate after a deliberate change: run this test with <c>SQM_WRITE_OPENAPI=1</c>, then review the
/// diff of <c>docs/integration/openapi/pi-integration-v1.json</c> before committing it.
/// </remarks>
public sealed class IntegrationOpenApiTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Committed = "docs/integration/openapi/pi-integration-v1.json";

    [Fact]
    public async Task The_committed_document_is_the_one_the_API_generates()
    {
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/openapi/integration-v1.json", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var generated = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!.AsObject();
        // The server URL is the host the document was asked from: not part of the contract.
        generated.Remove("servers");

        var path = Path.Combine(RepositoryRoot(), Committed);
        if (Environment.GetEnvironmentVariable("SQM_WRITE_OPENAPI") == "1")
        {
            await File.WriteAllTextAsync(
                path, generated.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n", TestContext.Current.CancellationToken);
            return;
        }

        var committed = JsonNode.Parse(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken))!;
        var difference = FirstDifference(committed, generated, "$");
        Assert.True(difference is null,
            $"{Committed} differs from what the API generates at {difference}. If the change is meant, regenerate with SQM_WRITE_OPENAPI=1 and review the diff.");
    }

    private static string? FirstDifference(JsonNode? expected, JsonNode? actual, string at)
    {
        switch (expected, actual)
        {
            case (null, null):
                return null;
            case (JsonObject e, JsonObject a):
                foreach (var key in e.Select(p => p.Key).Union(a.Select(p => p.Key)).Order(StringComparer.Ordinal))
                {
                    if (!e.ContainsKey(key) || !a.ContainsKey(key))
                    {
                        return $"{at}.{key} (only in the {(e.ContainsKey(key) ? "committed" : "generated")} document)";
                    }

                    if (FirstDifference(e[key], a[key], $"{at}.{key}") is { } inner)
                    {
                        return inner;
                    }
                }

                return null;
            case (JsonArray e, JsonArray a):
                if (e.Count != a.Count)
                {
                    return $"{at} ({e.Count} items committed, {a.Count} generated)";
                }

                for (var i = 0; i < e.Count; i++)
                {
                    if (FirstDifference(e[i], a[i], $"{at}[{i}]") is { } inner)
                    {
                        return inner;
                    }
                }

                return null;
            default:
                return JsonNode.DeepEquals(expected, actual) ? null : at;
        }
    }

    /// <summary>The repository: above the test's output when it is built in place, or above this source file when the output is elsewhere.</summary>
    private static string RepositoryRoot([CallerFilePath] string source = "")
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(source) ?? string.Empty })
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "global.json")))
                {
                    return directory.FullName;
                }
            }
        }

        throw new InvalidOperationException("No global.json above " + AppContext.BaseDirectory + " or " + source);
    }
}
