using System.Diagnostics;
using System.Net;
using DocHub.Api.Tests.Infrastructure;
using DocHub.Testing;
using DocHub.Testing.Database;

namespace DocHub.Api.Tests;

/// <summary>
/// T11 targets (tagged, run alone): track changes of a node with 200 changes ≤ 1.5 s, and the first page of a 31-day filtered
/// admin audit query &lt; 1 s — on 1 M log rows over 24 months (the weak-environment profile, decisions log Q19; the target
/// volume is 24 M).
/// </summary>
[Trait("Category", "Performance")]
[Collection(PerformanceTestGroup.Name)]
public sealed class HistoryPerformanceTests(HistoryPerformanceTests.Data data) : IClassFixture<HistoryPerformanceTests.Data>
{
    [Fact]
    public async Task Track_changes_of_a_node_with_200_changes_takes_under_1_5_s()
    {
        var arrange = new DocumentArrange(data);
        var (documentId, draft) = await arrange.CreateAsync();
        var node = (await ApiClient.ExpectAsync(data, TestUsers.Alice, HttpMethod.Post, $"/api/versions/{draft}/nodes", new { nodeTypeId = 1, title = "Busy" }, HttpStatusCode.Created))
            .GetProperty("id").GetInt32();
        Guid logical;
        List<long> entries;
        await using (var dbo = await SqlSession.OpenAsync(data.AdminConnectionString))
        {
            logical = await dbo.ScalarAsync<Guid>("SELECT LogicalNodeId FROM app.DocumentNode WHERE Id = @n;", ("@n", node));
            // 200 edits as the API would make them (acting user in the session context), each changing a few words of a long text.
            await dbo.ExecuteAsync(
                """
                EXEC sys.sp_set_session_context @key = N'UserId', @value = 2;
                DECLARE @i INT = 1, @text NVARCHAR (MAX);
                WHILE @i <= 200
                BEGIN
                    SET @text = (SELECT STRING_AGG(CONCAT(N'word', (n.k * 7 + CASE WHEN n.k % 20 = @i % 20 THEN @i ELSE 0 END) % 997), N' ')
                                 FROM (SELECT TOP (400) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS k FROM sys.all_objects) n);
                    UPDATE app.NodeContent
                    SET ContentJson = CONCAT(N'{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"', @text, N'"}]}]}'),
                        ContentHtml = N'', PlainText = @text, ContentHash = HASHBYTES('SHA2_256', @text), ModifiedAt = SYSUTCDATETIME()
                    WHERE NodeId = @n;
                    SET @i += 1;
                END;
                """,
                ("@n", node));
            entries = (await dbo.QueryAsync("SELECT TOP (8) Id FROM audit.ChangeLog WHERE LogicalNodeId = @l AND TableName = N'app.NodeContent' ORDER BY Id DESC;", ("@l", logical)))
                .Select(r => (long)r["Id"]!).ToList();
        }

        // Different "until" entries: each call computes (no cache hit); the last ones fold ~200 states.
        await ApiClient.ExpectAsync(data, TestUsers.Bob, HttpMethod.Get, $"/api/documents/{documentId}/nodes/{logical}/changes?since=d:2000-01-01&until=e:{entries[7]}", null, HttpStatusCode.OK);
        var times = new List<TimeSpan>();
        foreach (var entry in entries.Take(5))
        {
            var watch = Stopwatch.StartNew();
            await ApiClient.ExpectAsync(data, TestUsers.Bob, HttpMethod.Get, $"/api/documents/{documentId}/nodes/{logical}/changes?since=d:2000-01-01&until=e:{entry}", null, HttpStatusCode.OK);
            times.Add(watch.Elapsed);
        }

        var median = times.Order().ElementAt(2);
        Assert.True(median < PerformanceBudget.For(TimeSpan.FromSeconds(1.5)), $"median {median.TotalMilliseconds:F0} ms");
    }

    [Theory]
    [InlineData("&source=Script")]
    [InlineData("&ticket=INC-500")]
    [InlineData("&table=app.NodeContent&operation=U")]
    public async Task A_31_day_filtered_audit_page_takes_under_1_s(string filter)
    {
        var to = new DateTime(2026, 6, 20, 0, 0, 0, DateTimeKind.Utc);
        var path = $"/api/admin/audit?from={Uri.EscapeDataString(to.AddDays(-31).ToString("o"))}&to={Uri.EscapeDataString(to.ToString("o"))}{filter}&pageSize=50";
        for (var i = 0; i < 3; i++)
        {
            await ApiClient.ExpectAsync(data, TestUsers.Admin, HttpMethod.Get, path, null, HttpStatusCode.OK);
        }

        var times = new List<TimeSpan>();
        for (var i = 0; i < 5; i++)
        {
            var watch = Stopwatch.StartNew();
            var page = await ApiClient.ExpectAsync(data, TestUsers.Admin, HttpMethod.Get, path, null, HttpStatusCode.OK);
            times.Add(watch.Elapsed);
            Assert.True(page.GetProperty("items").GetArrayLength() > 0);
        }

        var median = times.Order().ElementAt(2);
        Assert.True(median < PerformanceBudget.For(TimeSpan.FromSeconds(1)), $"median {median.TotalMilliseconds:F0} ms");
    }

    public sealed class Data(SqlServerContainerFixture server) : DocHubApiFactory(server), IAsyncLifetime
    {
        public const int Rows = 1_000_000;

        private const int Batch = 50_000;

        async ValueTask IAsyncLifetime.InitializeAsync()
        {
            await InitializeAsync();
            await using var dbo = await SqlSession.OpenAsync(AdminConnectionString);
            // Synthetic log rows over 24 months (2025-01 … 2026-12), in batches; 1 in 50 from scripts with a ticket.
            for (var batch = 0; batch < Rows / Batch; batch++)
            {
                await dbo.ExecuteAsync(
                    """
                    WITH n AS (SELECT TOP (@batch) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) + @offset AS i FROM sys.all_objects a CROSS JOIN sys.all_objects b CROSS JOIN sys.all_objects c)
                    INSERT INTO audit.ChangeLog (ChangedAt, TableName, Operation, EntityId, DocumentId, DocumentVersionId, LogicalNodeId, OldValues, NewValues, ChangedColumns, UserId, Source, DbLogin, Ticket)
                    SELECT DATEADD(SECOND, CAST(i * @step AS INT), '2025-01-01'),
                           CASE i % 4 WHEN 0 THEN N'app.NodeContent' WHEN 1 THEN N'app.DocumentNode' WHEN 2 THEN N'app.Document' ELSE N'app.DocumentVersion' END,
                           CASE WHEN i % 10 = 0 THEN 'I' ELSE 'U' END, i % 100000, i % 20000, i % 40000, NULL, N'{"Title":"old"}', N'{"Title":"new"}', N'Title',
                           CASE WHEN i % 50 = 0 THEN NULL ELSE 2 + i % 5 END, CASE WHEN i % 50 = 0 THEN 'Script' ELSE 'App' END,
                           CASE WHEN i % 50 = 0 THEN N'support.jane' ELSE N'app_api' END, CASE WHEN i % 50 = 0 THEN CONCAT(N'INC-', i % 1000) END
                    FROM n;
                    """,
                    ("@offset", batch * Batch), ("@batch", Batch), ("@step", 730L * 86400 / Rows));
            }

            await dbo.ExecuteAsync("UPDATE STATISTICS audit.ChangeLog;");
        }
    }
}
