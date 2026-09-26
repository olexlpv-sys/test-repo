using System.Diagnostics;
using System.Net;
using DocHub.Api.Tests.Infrastructure;
using DocHub.Testing;
using DocHub.Testing.Database;

namespace DocHub.Api.Tests;

/// <summary>
/// T20 / FR-E5 targets (tagged, run alone): a 300-node document exports in ≤ 10 s and a 2 000-node document in ≤ 60 s — from the
/// request to a downloadable file, every node with two paragraphs, a TOC, header/footer and title page.
/// </summary>
[Trait("Category", "Performance")]
[Collection(PerformanceTestGroup.Name)]
public sealed class ExportPerformanceTests(ExportApiFactory factory) : IClassFixture<ExportApiFactory>
{
    [Theory]
    [InlineData(300, 10)]
    [InlineData(2000, 60)]
    public async Task A_document_exports_within_the_target(int nodes, int seconds)
    {
        var (_, draft) = await new DocumentArrange(factory).CreateAsync(title: $"Large {nodes}");
        await using (var dbo = await SqlSession.OpenAsync(factory.AdminConnectionString))
        {
            // One chapter per 20 nodes, the rest sections under them; each node with two paragraphs.
            await dbo.ExecuteAsync(
                """
                EXEC sys.sp_set_session_context @key = N'UserId', @value = 2;
                DECLARE @i INT = 0, @chapter INT, @id INT;
                WHILE @i < @n
                BEGIN
                    INSERT INTO app.DocumentNode (DocumentVersionId, LogicalNodeId, ParentNodeId, NodeTypeId, Title, SortOrder, CreatedByUserId, ModifiedByUserId)
                    VALUES (@v, NEWID(), CASE WHEN @i % 20 = 0 THEN NULL ELSE @chapter END, 1, CONCAT(N'Node ', @i), (@i + 1) * 1024, 2, 2);
                    SET @id = CAST(SCOPE_IDENTITY() AS INT);
                    IF @i % 20 = 0 SET @chapter = @id;
                    INSERT INTO app.NodeContent (NodeId, DocumentVersionId, LogicalNodeId, ContentJson, ContentHash, ModifiedByUserId)
                    SELECT n.Id, n.DocumentVersionId, n.LogicalNodeId,
                           N'{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"Lorem ipsum dolor sit amet, consectetur adipiscing elit, sed do eiusmod tempor incididunt ut labore et dolore magna aliqua."}]},{"type":"paragraph","content":[{"type":"text","text":"Ut enim ad minim veniam, quis nostrud exercitation ullamco laboris nisi ut aliquip ex ea commodo consequat."}]}]}',
                           HASHBYTES('SHA2_256', CONCAT(N'x', @i)), 2
                    FROM app.DocumentNode AS n WHERE n.Id = @id;
                    SET @i += 1;
                END;
                """,
                ("@n", nodes), ("@v", draft));
        }

        // A warm browser, as in a running instance.
        await Warm(draft);

        var watch = Stopwatch.StartNew();
        var job = await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Post, $"/api/versions/{draft}/exports/pdf", new { pageSize = "Letter" }, HttpStatusCode.Accepted);
        var id = job.GetProperty("jobId").GetInt32();
        string? status;
        do
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
            status = (await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Get, $"/api/exports/{id}", null, HttpStatusCode.OK)).GetProperty("status").GetString();
        }
        while (status is "Queued" or "Running" && watch.Elapsed < TimeSpan.FromSeconds(seconds * 3));

        watch.Stop();
        Assert.Equal("Succeeded", status);
        var budget = PerformanceBudget.For(TimeSpan.FromSeconds(seconds));
        Assert.True(watch.Elapsed <= budget, $"{nodes} nodes exported in {watch.Elapsed.TotalSeconds:0.0} s (budget {budget.TotalSeconds:0.0} s).");
        var render = factory.Renderer.Renders.Last();
        Assert.Equal(nodes, render.Document.Sections.Count);
    }

    private async Task Warm(int draft)
    {
        var job = await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Post, $"/api/versions/{draft}/exports/pdf", new { toc = false }, HttpStatusCode.Accepted);
        var id = job.GetProperty("jobId").GetInt32();
        var watch = Stopwatch.StartNew();
        while ((await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Get, $"/api/exports/{id}", null, HttpStatusCode.OK)).GetProperty("status").GetString() is "Queued" or "Running"
               && watch.Elapsed < TimeSpan.FromMinutes(3))
        {
            await Task.Delay(200, TestContext.Current.CancellationToken);
        }
    }
}
