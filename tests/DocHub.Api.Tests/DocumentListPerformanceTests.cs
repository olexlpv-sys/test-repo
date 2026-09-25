using System.Diagnostics;
using System.Net;
using DocHub.Api.Tests.Infrastructure;
using DocHub.Testing;
using DocHub.Testing.Database;

namespace DocHub.Api.Tests;

/// <summary>
/// FR-D5: a document list page of 50 in under 100 ms (budget per decisions log Q19) with the NFR-L2 document volume —
/// 10 000 documents in 100 folders, each with a signed version, every second one with a draft and two approvers.
/// </summary>
[Trait("Category", "Performance")]
[Collection(PerformanceTestGroup.Name)]
public sealed class DocumentListPerformanceTests(DocumentListPerformanceTests.Data data) : IClassFixture<DocumentListPerformanceTests.Data>
{
    [Theory]
    [InlineData("?pageSize=50")]
    [InlineData("?pageSize=50&sortBy=modifiedAt&sortDir=desc")]
    [InlineData("?pageSize=50&search=report")]
    public async Task A_page_of_50_takes_under_100_ms(string query)
    {
        var path = $"/api/folders/{data.Folder}/documents{query}";
        for (var i = 0; i < 3; i++)
        {
            await ApiClient.ExpectAsync(data, TestUsers.Carol, HttpMethod.Get, path, null, HttpStatusCode.OK);
        }

        var times = new List<TimeSpan>();
        for (var i = 0; i < 5; i++)
        {
            var watch = Stopwatch.StartNew();
            var page = await ApiClient.ExpectAsync(data, TestUsers.Carol, HttpMethod.Get, path, null, HttpStatusCode.OK);
            times.Add(watch.Elapsed);
            Assert.True(page.GetProperty("items").GetArrayLength() > 0);
        }

        var median = times.Order().ElementAt(2);
        Assert.True(median < PerformanceBudget.For(TimeSpan.FromMilliseconds(100)), $"median {median.TotalMilliseconds:F0} ms");
    }

    public sealed class Data(SqlServerContainerFixture server) : DocHubApiFactory(server), IAsyncLifetime
    {
        public int Folder { get; private set; }

        async ValueTask IAsyncLifetime.InitializeAsync()
        {
            await InitializeAsync();
            await using var dbo = await SqlSession.OpenAsync(AdminConnectionString);
            await dbo.ExecuteAsync("DISABLE TRIGGER ALL ON app.Folder; DISABLE TRIGGER ALL ON app.Document; DISABLE TRIGGER ALL ON app.DocumentVersion; DISABLE TRIGGER ALL ON app.DocumentPermission;");
            try
            {
                await dbo.ExecuteAsync(
                    """
                    WITH n AS (SELECT TOP (100) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects)
                    INSERT INTO app.Folder (ParentFolderId, Name, SortOrder, CreatedByUserId) SELECT 1, CONCAT(N'Perf ', i), i * 1024, 1 FROM n;
                    """);
                await dbo.ExecuteAsync(
                    """
                    WITH n AS (SELECT TOP (10000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects a CROSS JOIN sys.all_objects b)
                    INSERT INTO app.Document (FolderId, Title, OwnerUserId)
                    SELECT f.Id, CONCAT(CASE WHEN n.i % 5 = 0 THEN N'Quarterly report ' ELSE N'Document ' END, n.i), 2 + n.i % 3
                    FROM n JOIN (SELECT Id, ROW_NUMBER() OVER (ORDER BY Id) - 1 AS k FROM app.Folder WHERE Name LIKE N'Perf %') f ON f.k = n.i % 100;
                    """);
                await dbo.ExecuteAsync(
                    """
                    INSERT INTO app.DocumentVersion (DocumentId, Status, VersionNumber, SignedAt, CreatedByUserId, IsCurrent, SignedContentHash)
                    SELECT Id, 2, 1, SYSUTCDATETIME(), OwnerUserId, CASE WHEN Id % 2 = 0 THEN 0 ELSE 1 END, HASHBYTES('SHA2_256', N'v1') FROM app.Document WHERE Title NOT LIKE N'Doc %';
                    INSERT INTO app.DocumentVersion (DocumentId, Status, BasedOnVersionId, CreatedByUserId, IsCurrent)
                    SELECT v.DocumentId, 1, v.Id, 2, 1 FROM app.DocumentVersion v WHERE v.DocumentId % 2 = 0 AND v.Status = 2;
                    INSERT INTO app.DocumentPermission (DocumentId, UserId, Role, GrantedByUserId)
                    SELECT d.Id, u.UserId, 2, 2 FROM app.Document d CROSS JOIN (VALUES (5), (6)) AS u (UserId) WHERE d.Id % 2 = 0 AND d.Title NOT LIKE N'Doc %';
                    """);
            }
            finally
            {
                await dbo.ExecuteAsync("ENABLE TRIGGER ALL ON app.DocumentPermission; ENABLE TRIGGER ALL ON app.DocumentVersion; ENABLE TRIGGER ALL ON app.Document; ENABLE TRIGGER ALL ON app.Folder;");
            }

            Folder = await dbo.ScalarAsync<int>("SELECT MIN(Id) FROM app.Folder WHERE Name LIKE N'Perf %';");
        }
    }
}
