using System.Diagnostics;
using System.Net;
using System.Text.Json;
using DocHub.Api.Tests.Infrastructure;
using DocHub.Infrastructure.Export;
using DocHub.Testing.Database;

namespace DocHub.Api.Tests;

/// <summary>
/// T20 end to end: the worker renders with real Chromium and stores in Azurite. The FR-T1 example document, DRAFT watermark,
/// signature page, tampering banner, subtree export, cache reuse across users and its invalidation, the security fixtures and a
/// failed render.
/// </summary>
public sealed class ExportTests(ExportApiFactory factory) : IClassFixture<ExportApiFactory>
{
    private readonly DocumentArrange _arrange = new(factory);

    private static string Paragraphs(int count, string text = "Lorem ipsum dolor sit amet, consectetur adipiscing elit, sed do eiusmod tempor.") =>
        $$"""{"type":"doc","content":[{{string.Join(',', Enumerable.Repeat($$"""{"type":"paragraph","content":[{"type":"text","text":"{{text}}"}]}""", count))}}]}""";

    /// <summary>The FR-T1 example tree; chapters carry enough text to span pages.</summary>
    private async Task<(int DocumentId, int Draft)> ExampleAsync(string title)
    {
        var (documentId, draft) = await _arrange.CreateAsync(title: title);
        await using var dbo = await SqlSession.OpenAsync(factory.AdminConnectionString);
        async Task<int> Node(int? parent, string name, int order, int paragraphs)
        {
            var id = await dbo.Data.NodeAsync(draft, parent, name, order * 1024);
            await dbo.Data.ContentAsync(id, Paragraphs(paragraphs));
            return id;
        }

        var chapter1 = await Node(null, "Chapter 1", 1, 30);
        await Node(chapter1, "Section 1", 1, 5);
        var section2 = await Node(chapter1, "Section 2", 2, 5);
        await Node(section2, "Subsection 1", 1, 25);
        await Node(section2, "Subsection 2", 2, 5);
        var chapter2 = await Node(null, "Chapter 2", 2, 30);
        var section21 = await Node(chapter2, "Section 1", 1, 5);
        await Node(section21, "Subsection 1", 1, 5);
        await Node(section21, "Subsection 2", 2, 40);
        return (documentId, draft);
    }

    private Task<JsonElement> StartAsync(int user, int version, object? body = null) =>
        ApiClient.ExpectAsync(factory, user, HttpMethod.Post, $"/api/versions/{version}/exports/pdf", body ?? new { }, HttpStatusCode.Accepted);

    private async Task<JsonElement> WaitAsync(int user, JsonElement job)
    {
        var id = job.GetProperty("jobId").GetInt32();
        var watch = Stopwatch.StartNew();
        while (true)
        {
            var status = await ApiClient.ExpectAsync(factory, user, HttpMethod.Get, $"/api/exports/{id}", null, HttpStatusCode.OK);
            if (status.GetProperty("status").GetString() is "Succeeded" or "Failed" || watch.Elapsed > TimeSpan.FromSeconds(120))
            {
                return status;
            }

            await Task.Delay(200, TestContext.Current.CancellationToken);
        }
    }

    private async Task<byte[]> DownloadAsync(int user, JsonElement job, string? expectedFileName = null)
    {
        using var client = factory.CreateClientFor(user);
        using var response = await client.GetAsync(new Uri($"/api/exports/{job.GetProperty("jobId").GetInt32()}/file", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        if (expectedFileName is not null)
        {
            Assert.Equal(expectedFileName, response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        }

        return await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
    }

    private async Task<byte[]> ExportAsync(int user, int version, object? body = null)
    {
        var done = await WaitAsync(user, await StartAsync(user, version, body));
        Assert.True(done.GetProperty("status").GetString() == "Succeeded", done.ToString());
        return await DownloadAsync(user, done);
    }

    private static string Spaced(string text) => $" {text} ";

    [Fact]
    public async Task The_example_document_has_title_page_toc_with_correct_page_numbers_numbered_headings_and_page_x_of_y()
    {
        var (_, draft) = await ExampleAsync("Quality Manual");

        var job = await WaitAsync(TestUsers.Alice, await StartAsync(TestUsers.Alice, draft));
        Assert.Equal("Succeeded", job.GetProperty("status").GetString());
        Assert.Equal(100, job.GetProperty("progress").GetInt32());
        var pdf = await DownloadAsync(TestUsers.Alice, job, "Quality Manual - Draft.pdf");
        Assert.Equal(pdf.Length, job.GetProperty("fileSize").GetInt64());

        var pages = PdfText.Pages(pdf);
        Assert.True(pages.Count >= 6, $"{pages.Count} pages");
        Assert.Contains("Quality Manual", pages[0], StringComparison.Ordinal);
        Assert.Contains("Folder General", pages[0], StringComparison.Ordinal);
        Assert.Contains("Owner Alice Anderson", pages[0], StringComparison.Ordinal);
        Assert.Contains("Contents", pages[1], StringComparison.Ordinal);
        var toc = Spaced(pages[1]);
        var destinations = ChromiumPdfRenderer.Destinations(pdf);
        string[] headings = ["1 Chapter 1", "1.1 Section 1", "1.2 Section 2", "1.2.1 Subsection 1", "1.2.2 Subsection 2", "2 Chapter 2", "2.1 Section 1", "2.1.1 Subsection 1", "2.1.2 Subsection 2"];
        for (var i = 0; i < headings.Length; i++)
        {
            var page = destinations[PrintHtmlComposer.Anchor(i)];
            Assert.True(page > 2, $"{headings[i]} on page {page}");
            Assert.Contains(Spaced($"{headings[i]} {page}"), toc, StringComparison.Ordinal);
            Assert.Contains(Spaced(headings[i]), Spaced(pages[page - 1]), StringComparison.Ordinal);
        }

        Assert.NotEqual(destinations[PrintHtmlComposer.Anchor(0)], destinations[PrintHtmlComposer.Anchor(8)]);
        for (var i = 0; i < pages.Count; i++)
        {
            Assert.Contains($"Page {i + 1} of {pages.Count}", pages[i], StringComparison.Ordinal);
            Assert.Contains("Quality Manual", pages[i], StringComparison.Ordinal);
        }

        // Drafts carry the watermark on every page and no signature page.
        Assert.All(PdfText.Letters(pdf), p => Assert.Contains("DRAFT", p, StringComparison.Ordinal));
        Assert.DoesNotContain(pages, p => p.Contains("Signatures", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Signed_versions_get_the_signature_page_and_after_a_script_edit_the_warning_banner()
    {
        var (_, version, nodes) = await _arrange.SignedAsync();

        var signed = await ExportAsync(TestUsers.Bob, version);
        var pages = PdfText.Pages(signed);

        Assert.DoesNotContain(PdfText.Letters(signed), p => p.Contains("DRAFT", StringComparison.Ordinal));
        Assert.Contains("Version v1", pages[0], StringComparison.Ordinal);
        Assert.Contains("Signatures", pages[^1], StringComparison.Ordinal);
        Assert.Contains("Carol Clark", pages[^1], StringComparison.Ordinal);
        Assert.Contains("Dave Davis", pages[^1], StringComparison.Ordinal);
        Assert.DoesNotContain(pages, p => p.Contains("modified after signing", StringComparison.Ordinal));

        await _arrange.EditContentBySqlAsync(nodes[0]);
        var renders = factory.Renderer.Renders.Count;
        var tampered = PdfText.Pages(await ExportAsync(TestUsers.Bob, version));
        Assert.Equal(renders + 1, factory.Renderer.Renders.Count);
        Assert.All(tampered, p => Assert.Contains("Warning: this signed version was modified after signing.", p, StringComparison.Ordinal));

        var noHeader = PdfText.Pages(await ExportAsync(TestUsers.Bob, version, new { headerFooter = false }));
        Assert.All(noHeader, p => Assert.Contains("modified after signing", p, StringComparison.Ordinal));
        Assert.DoesNotContain(noHeader, p => p.Contains("Page 1 of", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_subtree_export_contains_only_the_node_and_its_descendants_with_their_numbers()
    {
        var (_, draft) = await ExampleAsync("Subtree Manual");
        var tree = await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Get, $"/api/versions/{draft}/tree", null, HttpStatusCode.OK);
        var section2 = tree[0].GetProperty("children")[1];
        Assert.Equal("Section 2", section2.GetProperty("title").GetString());

        var job = await WaitAsync(TestUsers.Alice, await StartAsync(TestUsers.Alice, draft, new { logicalNodeId = section2.GetProperty("logicalNodeId").GetGuid(), titlePage = false }));
        var text = Spaced(string.Join(' ', PdfText.Pages(await DownloadAsync(TestUsers.Alice, job, "Subtree Manual - Section 2 - Draft.pdf"))));

        Assert.Contains(" 1.2 Section 2 ", text, StringComparison.Ordinal);
        Assert.Contains(" 1.2.1 Subsection 1 ", text, StringComparison.Ordinal);
        Assert.Contains(" 1.2.2 Subsection 2 ", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Chapter", text, StringComparison.Ordinal);
        Assert.DoesNotContain("1.1 Section 1", text, StringComparison.Ordinal);
        Assert.DoesNotContain(" 2.1 ", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Signed_exports_are_served_from_cache_for_any_user_until_something_they_show_changes()
    {
        var (documentId, version, nodes) = await _arrange.SignedAsync();
        var first = await ExportAsync(TestUsers.Alice, version);
        var renders = factory.Renderer.Renders.Count;

        // Same user and another user: their own jobs, Succeeded at once, no render, same file.
        foreach (var user in new[] { TestUsers.Alice, TestUsers.Bob })
        {
            var job = await StartAsync(user, version);
            Assert.Equal("Succeeded", job.GetProperty("status").GetString());
            Assert.Equal(first, await DownloadAsync(user, job));
        }

        Assert.Equal(renders, factory.Renderer.Renders.Count);
        var jobs = await Db.QueryAsync(factory, "SELECT RequestedByUserId, Status, BlobPath FROM app.ExportJob WHERE DocumentVersionId = @v ORDER BY Id;", ("@v", version));
        Assert.Equal([TestUsers.Alice, TestUsers.Alice, TestUsers.Bob], jobs.Select(j => (int)j["RequestedByUserId"]!));
        Assert.Single(jobs.Select(j => (string)j["BlobPath"]!).Distinct());
        var audited = await Db.QueryAsync(factory, "SELECT UserId FROM audit.ChangeLog WHERE TableName = N'app.ExportJob' AND Operation = 'I' AND DocumentVersionId = @v;", ("@v", version));
        Assert.Equal(3, audited.Count);

        // Anything the PDF shows gives a new cache key: a script edit, a rename, a folder move, a style change.
        string[] changes =
        [
            $"UPDATE app.NodeContent SET ContentJson = JSON_MODIFY(ContentJson, '$.edited', 'x') WHERE NodeId = {nodes[0]};",
            $"UPDATE app.Document SET Title = N'Renamed' WHERE Id = {documentId};",
            $"INSERT INTO app.Folder (Name, SortOrder, CreatedByUserId) VALUES (N'Moved {Guid.NewGuid():N}', 99, 1); UPDATE app.Document SET FolderId = SCOPE_IDENTITY() WHERE Id = {documentId};",
            "UPDATE app.ContentStyle SET PropertiesJson = JSON_MODIFY(PropertiesJson, '$.fontSize', 24) WHERE StyleId = 'Normal';",
        ];
        foreach (var change in changes)
        {
            await using (var dbo = await SqlSession.OpenAsync(factory.AdminConnectionString))
            {
                await dbo.ExecuteAsync(change);
            }

            var job = await StartAsync(TestUsers.Bob, version);
            Assert.True(job.GetProperty("status").GetString() is "Queued" or "Running", change);
            Assert.Equal("Succeeded", (await WaitAsync(TestUsers.Bob, job)).GetProperty("status").GetString());
        }

        Assert.Equal(renders + changes.Length, factory.Renderer.Renders.Count);
    }

    [Fact]
    public async Task Hostile_titles_notes_and_links_are_printed_as_text_and_the_render_makes_no_request()
    {
        const string title = "<img src=http://internal-host/x>";
        var (documentId, draft) = await _arrange.CreateAsync(title: title);
        await using (var dbo = await SqlSession.OpenAsync(factory.AdminConnectionString))
        {
            var node = await dbo.Data.NodeAsync(draft, title: "<b>Node</b>");
            await dbo.Data.ContentAsync(node, """{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"see internal","marks":[{"type":"link","attrs":{"href":"http://internal-host/secret"}}]}]}]}""");
        }

        await _arrange.GrantAsync(documentId, TestUsers.Carol);
        await ApiClient.ExpectAsync(factory, TestUsers.Carol, HttpMethod.Post, $"/api/versions/{draft}/signatures", new { comment = "<script>alert(1)</script>" }, HttpStatusCode.OK);

        var before = factory.Renderer.Renders.Count;
        var text = string.Join(' ', PdfText.Pages(await ExportAsync(TestUsers.Alice, draft)));

        Assert.Contains("<img src=http://internal-host/x>", text, StringComparison.Ordinal);
        Assert.Contains("<script>alert(1)</script>", text, StringComparison.Ordinal);
        Assert.Contains("<b>Node</b>", text, StringComparison.Ordinal);
        Assert.Contains("see internal", text, StringComparison.Ordinal);
        var render = factory.Renderer.Renders.Skip(before).Single();
        Assert.Empty(render.Result.BlockedRequests);
    }

    [Fact]
    public async Task A_failed_render_marks_the_job_failed_and_the_worker_goes_on()
    {
        var (_, draft) = await _arrange.CreateAsync();
        await _arrange.AddNodesAsync(draft);

        factory.Renderer.FailNext = true;
        var failed = await WaitAsync(TestUsers.Alice, await StartAsync(TestUsers.Alice, draft));
        Assert.Equal("Failed", failed.GetProperty("status").GetString());
        Assert.Equal("The PDF could not be rendered.", failed.GetProperty("error").GetString());
        var file = await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Get, $"/api/exports/{failed.GetProperty("jobId").GetInt32()}/file", null, HttpStatusCode.Conflict);
        Assert.Equal("export-not-ready", file.GetProperty("type").GetString());

        // A new request (the failed job is not pending) renders normally.
        await ExportAsync(TestUsers.Alice, draft);
        var audit = await Db.QueryAsync(factory,
            "SELECT DISTINCT UserId FROM audit.ChangeLog WHERE TableName = N'app.ExportJob' AND Operation = 'U' AND DocumentVersionId = @v;", ("@v", draft));
        Assert.Equal([TestUsers.System], audit.Select(r => (int)r["UserId"]!));
    }
}
