using System.Diagnostics;
using System.Globalization;
using System.Net;
using DocHub.Api.Tests.Infrastructure;
using DocHub.Testing;
using DocHub.Testing.Database;

namespace DocHub.Api.Tests;

/// <summary>
/// T05 single-request targets on generated data (testing strategy: tagged performance tests): the user picker over
/// 50 500 users (NFR-L1) and the node-type / style grids with usage counts. The node volume defaults to the current
/// environment's profile (decisions log Q19); set <c>DOCHUB_PERF_NODES=15000000</c> for the NFR-L2 volume (T19 runs it).
/// </summary>
[Trait("Category", "Performance")]
[Collection(PerformanceTestGroup.Name)]
public sealed class DictionaryPerformanceTests(DictionaryPerformanceTests.Data data) : IClassFixture<DictionaryPerformanceTests.Data>
{
    [Fact]
    public async Task User_search_over_50500_users_returns_a_page_in_under_100_ms()
    {
        var (elapsed, body) = await TimeAsync("/api/users?search=car&pageSize=20");

        Assert.InRange(body.GetProperty("items").GetArrayLength(), 1, 20);
        Assert.True(body.GetProperty("totalCount").GetInt32() > 1000, "1 in 50 generated users match the prefix");
        Assert.True(elapsed < PerformanceBudget.For(TimeSpan.FromMilliseconds(100)), $"took {elapsed.TotalMilliseconds:F0} ms");
    }

    [Fact]
    public async Task Node_type_and_style_grids_with_usage_counts_take_under_1_s()
    {
        var (types, typeBody) = await TimeAsync("/api/node-types?includeInactive=true");
        var (styles, styleBody) = await TimeAsync("/api/content-styles?includeInactive=true");

        Assert.Contains(typeBody.EnumerateArray(), t => t.GetProperty("usageCount").GetInt32() >= data.Nodes / 2);
        Assert.Contains(styleBody.EnumerateArray(), s => s.GetProperty("usageCount").GetInt32() == data.Nodes);
        Assert.True(types < PerformanceBudget.For(TimeSpan.FromSeconds(1)), $"node types took {types.TotalMilliseconds:F0} ms");
        Assert.True(styles < PerformanceBudget.For(TimeSpan.FromSeconds(1)), $"styles took {styles.TotalMilliseconds:F0} ms");
    }

    /// <summary>Three warm-up requests (plan compilation, statistics after the bulk load, JIT), then the median of five.</summary>
    private async Task<(TimeSpan Elapsed, System.Text.Json.JsonElement Body)> TimeAsync(string path)
    {
        System.Text.Json.JsonElement body = default;
        for (var i = 0; i < 3; i++)
        {
            body = await ApiClient.ExpectAsync(data, TestUsers.Admin, HttpMethod.Get, path, null, HttpStatusCode.OK);
        }

        var times = new List<TimeSpan>();
        for (var i = 0; i < 5; i++)
        {
            var watch = Stopwatch.StartNew();
            body = await ApiClient.ExpectAsync(data, TestUsers.Admin, HttpMethod.Get, path, null, HttpStatusCode.OK);
            times.Add(watch.Elapsed);
        }

        return (times.Order().ElementAt(times.Count / 2), body);
    }

    /// <summary>The API with generated users, nodes, contents and style usages (bulk-loaded with the audit triggers off).</summary>
    public sealed class Data(SqlServerContainerFixture server) : DocHubApiFactory(server), IAsyncLifetime
    {
        private const int Batch = 25_000;

        public int Nodes { get; } = int.TryParse(Environment.GetEnvironmentVariable("DOCHUB_PERF_NODES"), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 100_000;

        async ValueTask IAsyncLifetime.InitializeAsync()
        {
            await InitializeAsync();
            await using var dbo = await SqlSession.OpenAsync(AdminConnectionString);
            var versionId = await dbo.Data.VersionAsync(await dbo.Data.DocumentAsync());
            // Batches keep every command well under the 30 s command timeout on the current environment.
            await dbo.ExecuteAsync("DISABLE TRIGGER ALL ON app.[User]; DISABLE TRIGGER ALL ON app.DocumentNode; DISABLE TRIGGER ALL ON app.NodeContent;");
            try
            {
                await dbo.ExecuteAsync(
                    """
                    WITH n AS (SELECT TOP (50500) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects a CROSS JOIN sys.all_objects b)
                    INSERT INTO app.[User] (Login, DisplayName, Email, IsAdmin, IsActive)
                    SELECT CONCAT(CASE WHEN i % 50 = 0 THEN N'car' ELSE CHAR(97 + i % 26) + CHAR(97 + (i / 26) % 26) END, N'.user', i),
                           CONCAT(N'Generated User ', i), CONCAT(N'user', i, N'@load.local'), 0, 1
                    FROM n;
                    """);
                for (var offset = 0; offset < Nodes; offset += Batch)
                {
                    await dbo.ExecuteAsync(
                        """
                        WITH n AS (SELECT TOP (@count) @offset + ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects a CROSS JOIN sys.all_objects b)
                        INSERT INTO app.DocumentNode (DocumentVersionId, LogicalNodeId, NodeTypeId, Title, SortOrder, CreatedByUserId, ModifiedByUserId)
                        SELECT @v, NEWID(), 1 + i % 2, CONCAT(N'Node ', i), i, 2, 2 FROM n;

                        INSERT INTO app.NodeContent (NodeId, DocumentVersionId, LogicalNodeId, ContentHash, ModifiedByUserId)
                        SELECT d.Id, d.DocumentVersionId, d.LogicalNodeId, HASHBYTES('SHA2_256', N''), 2
                        FROM app.DocumentNode AS d
                        WHERE d.DocumentVersionId = @v AND NOT EXISTS (SELECT 1 FROM app.NodeContent AS c WHERE c.NodeId = d.Id);
                        """,
                        ("@count", Math.Min(Batch, Nodes - offset)), ("@offset", offset), ("@v", versionId));
                }

                await dbo.ExecuteAsync("INSERT INTO app.ContentStyleUsage (StyleId, NodeId) SELECT 'Normal', NodeId FROM app.NodeContent WHERE DocumentVersionId = @v;", ("@v", versionId));
            }
            finally
            {
                await dbo.ExecuteAsync("ENABLE TRIGGER ALL ON app.NodeContent; ENABLE TRIGGER ALL ON app.DocumentNode; ENABLE TRIGGER ALL ON app.[User];");
            }
        }
    }
}
