using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocHub.Api.Tests.Infrastructure;

namespace DocHub.Api.Tests;

/// <summary>
/// Contract test: the served OpenAPI document must equal the committed <c>src/DocHub.Api/openapi.v1.json</c> — the
/// contract the SPA's TypeScript client is generated from. Set <c>UPDATE_OPENAPI_SNAPSHOT=1</c> to accept a change.
/// </summary>
public sealed class OpenApiSnapshotTests(DocHubApiFactory factory) : IClassFixture<DocHubApiFactory>
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    [Fact]
    public async Task Served_openapi_document_matches_the_committed_snapshot()
    {
        using var client = factory.CreateClientFor(null);
        var served = JsonNode.Parse(await client.GetStringAsync(new Uri("/openapi/v1.json", UriKind.Relative), TestContext.Current.CancellationToken))!;
        var actual = served.ToJsonString(Indented).ReplaceLineEndings("\n") + "\n";
        var path = Path.Combine(RepositoryRoot(), "src", "DocHub.Api", "openapi.v1.json");

        if (Environment.GetEnvironmentVariable("UPDATE_OPENAPI_SNAPSHOT") == "1")
        {
            await File.WriteAllTextAsync(path, actual, TestContext.Current.CancellationToken);
        }

        Assert.True(File.Exists(path), $"Missing {path}. Run the tests with UPDATE_OPENAPI_SNAPSHOT=1 and commit the file.");
        var expected = (await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)).ReplaceLineEndings("\n");
        Assert.True(expected == actual, "The API contract changed. Review the diff, then run with UPDATE_OPENAPI_SNAPSHOT=1 and commit src/DocHub.Api/openapi.v1.json.");
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DocHub.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
