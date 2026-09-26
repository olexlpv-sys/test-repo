using System.Net;
using System.Text.Json;
using DocHub.Api.Tests.Infrastructure;
using DocHub.Testing.Database;

namespace DocHub.Api.Tests;

/// <summary>
/// T20 job API without a worker (jobs stay queued): one job per request and requester, pending-job reuse, visibility (FR-P5)
/// and the audit row of every export (FR-E7).
/// </summary>
public sealed class ExportJobTests(DocHubApiFactory factory) : IClassFixture<DocHubApiFactory>
{
    private readonly DocumentArrange _arrange = new(factory);

    private Task<JsonElement> StartAsync(int user, int version, object? body = null, HttpStatusCode expected = HttpStatusCode.Accepted) =>
        ApiClient.ExpectAsync(factory, user, HttpMethod.Post, $"/api/versions/{version}/exports/pdf", body ?? new { }, expected);

    [Fact]
    public async Task A_pending_job_of_the_caller_is_reused_other_users_and_options_get_their_own()
    {
        var (_, draft) = await _arrange.CreateAsync();
        await _arrange.AddNodesAsync(draft);

        var first = await StartAsync(TestUsers.Alice, draft);
        var again = await StartAsync(TestUsers.Alice, draft, new { pageSize = "A4", titlePage = true, toc = true, headerFooter = true, signaturePage = true });
        var letter = await StartAsync(TestUsers.Alice, draft, new { pageSize = "Letter" });
        var bob = await StartAsync(TestUsers.Bob, draft);

        Assert.Equal("Queued", first.GetProperty("status").GetString());
        Assert.Equal(0, first.GetProperty("progress").GetInt32());
        Assert.Equal(first.GetProperty("jobId").GetInt32(), again.GetProperty("jobId").GetInt32());
        Assert.NotEqual(first.GetProperty("jobId").GetInt32(), letter.GetProperty("jobId").GetInt32());
        Assert.NotEqual(first.GetProperty("jobId").GetInt32(), bob.GetProperty("jobId").GetInt32());
        Assert.EndsWith(" - Draft.pdf", first.GetProperty("fileName").GetString(), StringComparison.Ordinal);

        // Every job is an audited insert by its requester (FR-E7).
        var audit = await Db.QueryAsync(factory,
            "SELECT UserId, JSON_VALUE(NewValues, '$.OptionsJson') AS Options FROM audit.ChangeLog WHERE TableName = N'app.ExportJob' AND Operation = 'I' AND DocumentVersionId = @v ORDER BY Id;",
            ("@v", draft));
        Assert.Equal([TestUsers.Alice, TestUsers.Alice, TestUsers.Bob], audit.Select(r => (int)r["UserId"]!));
        Assert.Contains("\"pageSize\":\"Letter\"", (string)audit[1]["Options"]!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Jobs_are_visible_to_their_requester_and_admins_only_and_files_need_a_finished_job()
    {
        var (_, draft) = await _arrange.CreateAsync();
        var job = (await StartAsync(TestUsers.Bob, draft)).GetProperty("jobId").GetInt32();

        await ApiClient.ExpectAsync(factory, TestUsers.Bob, HttpMethod.Get, $"/api/exports/{job}", null, HttpStatusCode.OK);
        await ApiClient.ExpectAsync(factory, TestUsers.Admin, HttpMethod.Get, $"/api/exports/{job}", null, HttpStatusCode.OK);
        await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Get, $"/api/exports/{job}", null, HttpStatusCode.NotFound);
        await ApiClient.ExpectAsync(factory, TestUsers.Carol, HttpMethod.Get, $"/api/exports/{job}/file", null, HttpStatusCode.NotFound);
        await ApiClient.ExpectAsync(factory, TestUsers.Bob, HttpMethod.Get, $"/api/exports/{job + 100000}", null, HttpStatusCode.NotFound);
        var notReady = await ApiClient.ExpectAsync(factory, TestUsers.Bob, HttpMethod.Get, $"/api/exports/{job}/file", null, HttpStatusCode.Conflict);
        Assert.Equal("export-not-ready", notReady.GetProperty("type").GetString());
    }

    [Fact]
    public async Task Deleted_documents_are_exported_only_by_owner_or_admin_and_earlier_jobs_of_others_disappear()
    {
        var (documentId, draft) = await _arrange.CreateAsync();
        var bobs = (await StartAsync(TestUsers.Bob, draft)).GetProperty("jobId").GetInt32();
        var rowVersion = Uri.EscapeDataString(await _arrange.RowVersionAsync(documentId));
        await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Delete, $"/api/documents/{documentId}?rowVersion={rowVersion}", null, HttpStatusCode.NoContent);

        await StartAsync(TestUsers.Bob, draft, expected: HttpStatusCode.NotFound);
        await ApiClient.ExpectAsync(factory, TestUsers.Bob, HttpMethod.Get, $"/api/exports/{bobs}", null, HttpStatusCode.NotFound);
        await ApiClient.ExpectAsync(factory, TestUsers.Bob, HttpMethod.Get, $"/api/exports/{bobs}/file", null, HttpStatusCode.NotFound);
        await StartAsync(TestUsers.Alice, draft);
        await StartAsync(TestUsers.Admin, draft);
    }

    [Fact]
    public async Task Unknown_versions_and_nodes_are_404_and_bad_options_400()
    {
        var (_, draft) = await _arrange.CreateAsync();

        await StartAsync(TestUsers.Alice, 999_999, expected: HttpStatusCode.NotFound);
        await StartAsync(TestUsers.Alice, draft, new { logicalNodeId = Guid.NewGuid() }, HttpStatusCode.NotFound);
        await StartAsync(TestUsers.Alice, draft, new { pageSize = "A3" }, HttpStatusCode.BadRequest);
        await StartAsync(TestUsers.Alice, draft, new { toc = "yes" }, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Export_jobs_appear_in_the_document_history_once_without_worker_updates()
    {
        var (documentId, draft) = await _arrange.CreateAsync();
        var job = (await StartAsync(TestUsers.Alice, draft)).GetProperty("jobId").GetInt32();
        await using (var dbo = await SqlSession.OpenAsync(factory.AdminConnectionString))
        {
            // The worker's progress updates (App changes by the system user).
            await dbo.ExecuteAsync(
                "EXEC sys.sp_set_session_context @key = N'UserId', @value = 0; UPDATE app.ExportJob SET Status = 1, Progress = 50 WHERE Id = @j;",
                ("@j", job));
        }

        var history = await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Get, $"/api/documents/{documentId}/history", null, HttpStatusCode.OK);
        var entries = history.GetProperty("items").EnumerateArray().Where(e => e.GetProperty("kind").GetString() == "PdfExportRequested").ToList();
        var entry = Assert.Single(entries);
        Assert.Equal("PDF export requested", entry.GetProperty("summary").GetString());
    }
}
