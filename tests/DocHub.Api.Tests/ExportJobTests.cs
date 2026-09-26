using System.Net;
using System.Text.Json;
using DocHub.Api.Exports;
using DocHub.Api.Tests.Infrastructure;
using DocHub.Testing.Database;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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

    private PdfExportWorker Worker => factory.Services.GetServices<IHostedService>().OfType<PdfExportWorker>().Single();

    /// <summary>Marks a job Running since <paramref name="minutesAgo"/> minutes, as a worker would have.</summary>
    private async Task RunningSinceAsync(int job, int minutesAgo)
    {
        await using var dbo = await SqlSession.OpenAsync(factory.AdminConnectionString);
        await dbo.ExecuteAsync(
            "EXEC sys.sp_set_session_context @key = N'UserId', @value = 0; UPDATE app.ExportJob SET Status = 1, Progress = 40, StartedAt = DATEADD(MINUTE, -@m, SYSUTCDATETIME()) WHERE Id = @j;",
            ("@j", job), ("@m", minutesAgo));
    }

    private async Task<(byte Status, string? Error)> JobRowAsync(int job)
    {
        var row = (await Db.QueryAsync(factory, "SELECT Status, Error FROM app.ExportJob WHERE Id = @j;", ("@j", job))).Single();
        return ((byte)row["Status"]!, row["Error"] as string);
    }

    [Fact]
    public async Task A_job_left_running_past_the_timeout_is_not_reused_and_the_sweep_fails_it()
    {
        var (_, draft) = await _arrange.CreateAsync();
        var stuck = (await StartAsync(TestUsers.Alice, draft)).GetProperty("jobId").GetInt32();
        await RunningSinceAsync(stuck, 10);

        var retry = await StartAsync(TestUsers.Alice, draft);
        Assert.NotEqual(stuck, retry.GetProperty("jobId").GetInt32());

        // A job running within the timeout is still the caller's pending job.
        var fresh = retry.GetProperty("jobId").GetInt32();
        await RunningSinceAsync(fresh, 1);
        Assert.Equal(fresh, (await StartAsync(TestUsers.Alice, draft)).GetProperty("jobId").GetInt32());

        Assert.True(await Worker.FailStaleAsync(TestContext.Current.CancellationToken) >= 1);
        Assert.Equal((byte)3, (await JobRowAsync(stuck)).Status);
        Assert.Equal("The export was interrupted; export again.", (await JobRowAsync(stuck)).Error);
        Assert.Equal((byte)1, (await JobRowAsync(fresh)).Status);
    }

    [Fact]
    public async Task A_job_interrupted_by_shutdown_goes_back_to_the_queue()
    {
        var (_, draft) = await _arrange.CreateAsync();
        var job = (await StartAsync(TestUsers.Alice, draft)).GetProperty("jobId").GetInt32();
        await RunningSinceAsync(job, 0);

        using var stopping = new CancellationTokenSource();
        await stopping.CancelAsync();
        await Worker.ProcessAsync(job, stopping.Token);

        var status = await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Get, $"/api/exports/{job}", null, HttpStatusCode.OK);
        Assert.Equal(("Queued", 0), (status.GetProperty("status").GetString(), status.GetProperty("progress").GetInt32()));
    }

    [Fact]
    public async Task Exporting_a_discarded_draft_does_not_move_it_as_a_change_summary_baseline()
    {
        var (documentId, v1, nodes) = await _arrange.SignedAsync();
        var discarded = (await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Post, $"/api/documents/{documentId}/drafts", null, HttpStatusCode.Created)).GetProperty("id").GetInt32();
        var rowVersion = Uri.EscapeDataString((await _arrange.VersionAsync(discarded)).GetProperty("rowVersion").GetString()!);
        await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Delete, $"/api/versions/{discarded}?rowVersion={rowVersion}", null, HttpStatusCode.NoContent);
        var draft = (await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Post, $"/api/documents/{documentId}/drafts", null, HttpStatusCode.Created)).GetProperty("id").GetInt32();
        Guid logical;
        int node;
        await using (var dbo = await SqlSession.OpenAsync(factory.AdminConnectionString))
        {
            logical = await dbo.ScalarAsync<Guid>("SELECT LogicalNodeId FROM app.DocumentNode WHERE Id = @n;", ("@n", nodes[0]));
            node = await dbo.ScalarAsync<int>("SELECT Id FROM app.DocumentNode WHERE DocumentVersionId = @v AND LogicalNodeId = @l;", ("@v", draft), ("@l", logical));
        }

        var content = (await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Get, $"/api/nodes/{node}/content", null, HttpStatusCode.OK)).GetProperty("rowVersion").GetString();
        await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Put, $"/api/nodes/{node}/content",
            new { contentJson = System.Text.Json.Nodes.JsonNode.Parse("""{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"edited in the new draft"}]}]}"""), rowVersion = content },
            HttpStatusCode.OK);

        // Someone exports the discarded draft after the edit.
        await StartAsync(TestUsers.Bob, discarded);

        var summary = await ApiClient.ExpectAsync(factory, TestUsers.Bob, HttpMethod.Get, $"/api/versions/{draft}/change-summary?since=v:{discarded}", null, HttpStatusCode.OK);
        var entry = summary.GetProperty("nodes").EnumerateArray().Single(n => n.GetProperty("logicalNodeId").GetGuid() == logical);
        Assert.Equal(1, entry.GetProperty("changeCount").GetInt32());
        Assert.NotEqual(v1, draft);
    }
}
