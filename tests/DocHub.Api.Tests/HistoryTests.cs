using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocHub.Api.Documents;
using DocHub.Api.Tests.Infrastructure;
using DocHub.Testing.Database;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DocHub.Api.Tests;

/// <summary>T11: change history of nodes and documents, entry diffs and content, the admin audit log.</summary>
public sealed class HistoryTests(DocHubApiFactory factory) : IClassFixture<DocHubApiFactory>
{
    private readonly DocumentArrange _arrange = new(factory);

    private static string Paragraph(string text) => $$"""{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"{{text}}"}]}]}""";

    [Fact]
    public async Task Node_history_spans_versions_with_signing_and_one_copy_entry()
    {
        var (documentId, draft) = await _arrange.CreateAsync();
        var node = await AddNodeAsync(draft, "Scope");
        var logical = await LogicalAsync(node);
        await _arrange.GrantAsync(documentId, TestUsers.Erin, role: 1);
        await SaveAsync(node, Paragraph("one"), TestUsers.Alice);
        await SaveAsync(node, Paragraph("one two"), TestUsers.Erin);
        await SaveAsync(node, Paragraph("one two three"), TestUsers.Alice);
        await _arrange.GrantAsync(documentId, TestUsers.Carol);
        await _arrange.SignAsync(draft, TestUsers.Carol);
        var draft2 = (await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Post, $"/api/documents/{documentId}/drafts", null, HttpStatusCode.Created)).GetProperty("id").GetInt32();
        var node2 = await NodeOfAsync(draft2, logical);
        await SaveAsync(node2, Paragraph("one two three four"), TestUsers.Erin);

        var history = await ApiClient.ExpectAsync(factory, TestUsers.Bob, HttpMethod.Get, $"/api/documents/{documentId}/nodes/{logical}/history", null, HttpStatusCode.OK);
        var entries = history.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(
            ["ContentChanged", "CopiedToNewDraft", "VersionSigned", "ContentChanged", "ContentChanged", "ContentChanged", "NodeCreated"],
            entries.Select(e => e.GetProperty("kind").GetString()));
        Assert.Equal([TestUsers.Erin, TestUsers.Alice, TestUsers.Alice, TestUsers.Erin, TestUsers.Alice, TestUsers.Alice],
            entries.Where(e => e.GetProperty("kind").GetString() is "ContentChanged" or "CopiedToNewDraft" or "NodeCreated")
                .Select(e => e.GetProperty("user").GetProperty("id").GetInt32()));
        Assert.Equal("Draft (based on v1)", entries[0].GetProperty("versionLabel").GetString());
        Assert.Equal("v1", entries[3].GetProperty("versionLabel").GetString());
        Assert.Equal("Content changed (+1 / −0 words)", entries[0].GetProperty("summary").GetString());
        Assert.True(entries[0].GetProperty("hasContentDiff").GetBoolean());
        Assert.All(entries, e => Assert.Equal("App", e.GetProperty("source").GetString()));
        Assert.Equal(7, history.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task Rename_and_type_change_in_one_request_is_one_entry()
    {
        var (documentId, draft) = await _arrange.CreateAsync();
        var node = await AddNodeAsync(draft, "Section 1");
        var rowVersion = (await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Get, $"/api/nodes/{node}", null, HttpStatusCode.OK)).GetProperty("rowVersion").GetString();
        await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Patch, $"/api/nodes/{node}", new { title = "Scope", nodeTypeId = 2, rowVersion }, HttpStatusCode.OK);

        var entry = (await HistoryAsync(documentId, await LogicalAsync(node)))[0];
        Assert.Equal("NodeRenamed", entry.GetProperty("kind").GetString());
        var changes = entry.GetProperty("changes").EnumerateArray().Select(c => (c.GetProperty("field").GetString(), c.GetProperty("old").GetString(), c.GetProperty("new").GetString())).ToList();
        Assert.Contains(("title", "Section 1", "Scope"), changes);
        Assert.Contains(("nodeTypeId", "1", "2"), changes);
    }

    [Fact]
    public async Task Script_changes_show_login_ticket_and_reason_and_after_signing_flags()
    {
        var (documentId, v1, nodes) = await _arrange.SignedAsync();
        var logical = await LogicalAsync(nodes[0]);
        await using (var dbo = await SqlSession.OpenAsync(factory.AdminConnectionString))
        {
            await dbo.ExecuteAsync("UPDATE app.NodeContent SET ContentJson = @j WHERE NodeId = @n;", ("@j", Paragraph("plain script")), ("@n", nodes[0]));
            await dbo.ExecuteAsync(
                "EXEC audit.usp_SetSupportContext @Ticket = N'INC-42', @Reason = N'Typo fix'; UPDATE app.NodeContent SET ContentJson = @j WHERE NodeId = @n;",
                ("@j", Paragraph("ticketed script")), ("@n", nodes[0]));
        }

        var entries = await HistoryAsync(documentId, logical);
        var (ticketed, plain) = (entries[0], entries[1]);
        Assert.Equal(("Script", "INC-42", "Typo fix"), (ticketed.GetProperty("source").GetString(), ticketed.GetProperty("ticket").GetString(), ticketed.GetProperty("reason").GetString()));
        Assert.Equal("Script", plain.GetProperty("source").GetString());
        Assert.False(string.IsNullOrEmpty(plain.GetProperty("dbLogin").GetString()));
        Assert.Equal(JsonValueKind.Null, plain.GetProperty("user").ValueKind);
        Assert.True(plain.GetProperty("afterSigning").GetBoolean());
        Assert.True(ticketed.GetProperty("afterSigning").GetBoolean());
        Assert.False(entries.Single(e => e.GetProperty("kind").GetString() == "VersionSigned").GetProperty("afterSigning").GetBoolean());
        Assert.Equal(v1, ticketed.GetProperty("versionId").GetInt32());

        // Derived-only rebuilds never appear.
        var refresher = factory.Services.GetServices<IHostedService>().OfType<DerivedContentRefresher>().Single();
        await refresher.RunOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(entries.Count, (await HistoryAsync(documentId, logical)).Count);
    }

    [Fact]
    public async Task Entry_content_is_the_historical_state_and_diffs_show_formatting_changes()
    {
        var (documentId, draft) = await _arrange.CreateAsync();
        var node = await AddNodeAsync(draft, "Old title");
        await SaveAsync(node, Paragraph("Make Scope bold"), TestUsers.Alice);
        await SaveAsync(node, """{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"Make "},{"type":"text","text":"Scope","marks":[{"type":"bold"}]},{"type":"text","text":" bold"}]}]}""", TestUsers.Alice);
        var rowVersion = (await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Get, $"/api/nodes/{node}", null, HttpStatusCode.OK)).GetProperty("rowVersion").GetString();
        await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Patch, $"/api/nodes/{node}", new { title = "New title", rowVersion }, HttpStatusCode.OK);

        var entries = await HistoryAsync(documentId, await LogicalAsync(node));
        Assert.Equal(["NodeRenamed", "ContentChanged", "ContentChanged", "NodeCreated"], entries.Select(e => e.GetProperty("kind").GetString()));
        Assert.Equal("Formatting changed", entries[1].GetProperty("summary").GetString());

        var diff = await ApiClient.ExpectAsync(factory, TestUsers.Bob, HttpMethod.Get, $"/api/history/entries/{entries[1].GetProperty("id").GetInt64()}/diff", null, HttpStatusCode.OK);
        var ops = diff.GetProperty("blocks")[0].GetProperty("ops").EnumerateArray().ToList();
        Assert.Equal(["equal", "format", "equal"], ops.Select(o => o.GetProperty("op").GetString()));
        Assert.Equal("bold added", ops[1].GetProperty("changes")[0].GetString());
        Assert.Equal(0, diff.GetProperty("stats").GetProperty("inserted").GetInt32());

        // As of the first content save: the old title and the plain text.
        var content = await ApiClient.ExpectAsync(factory, TestUsers.Bob, HttpMethod.Get, $"/api/history/entries/{entries[2].GetProperty("id").GetInt64()}/content", null, HttpStatusCode.OK);
        Assert.Equal("Old title", content.GetProperty("title").GetString());
        Assert.Equal("<p>Make Scope bold</p>", content.GetProperty("contentHtml").GetString());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Paragraph("Make Scope bold")), JsonNode.Parse(content.GetProperty("contentJson").GetRawText())));
        var renamed = await ApiClient.ExpectAsync(factory, TestUsers.Bob, HttpMethod.Get, $"/api/history/entries/{entries[0].GetProperty("id").GetInt64()}/content", null, HttpStatusCode.OK);
        Assert.Equal("New title", renamed.GetProperty("title").GetString());
        Assert.Contains("<strong>Scope</strong>", renamed.GetProperty("contentHtml").GetString()!, StringComparison.Ordinal);

        await ApiClient.ExpectAsync(factory, TestUsers.Bob, HttpMethod.Get, $"/api/history/entries/{entries[0].GetProperty("id").GetInt64()}/diff", null, HttpStatusCode.NotFound);
        await ApiClient.ExpectAsync(factory, TestUsers.Bob, HttpMethod.Get, "/api/history/entries/999999999/content", null, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Deleted_documents_hide_their_history_from_other_users()
    {
        var (documentId, draft) = await _arrange.CreateAsync();
        var node = await AddNodeAsync(draft, "Scope");
        await SaveAsync(node, Paragraph("text"), TestUsers.Alice);
        var logical = await LogicalAsync(node);
        var entry = (await HistoryAsync(documentId, logical))[0].GetProperty("id").GetInt64();
        await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Delete, $"/api/documents/{documentId}?rowVersion={Uri.EscapeDataString(await _arrange.RowVersionAsync(documentId))}", null, HttpStatusCode.NoContent);

        foreach (var (user, expected) in new[] { (TestUsers.Bob, HttpStatusCode.NotFound), (TestUsers.Alice, HttpStatusCode.OK), (TestUsers.Admin, HttpStatusCode.OK) })
        {
            await ApiClient.ExpectAsync(factory, user, HttpMethod.Get, $"/api/documents/{documentId}/nodes/{logical}/history", null, expected);
            await ApiClient.ExpectAsync(factory, user, HttpMethod.Get, $"/api/documents/{documentId}/history", null, expected);
            await ApiClient.ExpectAsync(factory, user, HttpMethod.Get, $"/api/history/entries/{entry}/diff", null, expected);
            await ApiClient.ExpectAsync(factory, user, HttpMethod.Get, $"/api/history/entries/{entry}/content", null, expected);
        }
    }

    [Fact]
    public async Task Document_history_lists_all_activity_and_filters()
    {
        var (documentId, draft) = await _arrange.CreateAsync();
        var node = await AddNodeAsync(draft, "Scope");
        await SaveAsync(node, Paragraph("text"), TestUsers.Alice);
        await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Post, $"/api/documents/{documentId}/permissions", new { userId = TestUsers.Carol, role = "Approver" }, HttpStatusCode.Created);
        await _arrange.SignAsync(draft, TestUsers.Carol);

        var kinds = (await ApiClient.ExpectAsync(factory, TestUsers.Bob, HttpMethod.Get, $"/api/documents/{documentId}/history?pageSize=200", null, HttpStatusCode.OK))
            .GetProperty("items").EnumerateArray().Select(e => e.GetProperty("kind").GetString()).ToList();
        foreach (var kind in new[] { "DocumentCreated", "VersionCreated", "NodeCreated", "ContentChanged", "PermissionGranted", "SignatureAdded", "VersionSigned" })
        {
            Assert.Contains(kind, kinds);
        }

        var carol = await ApiClient.ExpectAsync(factory, TestUsers.Bob, HttpMethod.Get, $"/api/documents/{documentId}/history?userId={TestUsers.Carol}", null, HttpStatusCode.OK);
        Assert.All(carol.GetProperty("items").EnumerateArray(), e => Assert.Equal(TestUsers.Carol, e.GetProperty("user").GetProperty("id").GetInt32()));
        Assert.Equal(0, (await ApiClient.ExpectAsync(factory, TestUsers.Bob, HttpMethod.Get, $"/api/documents/{documentId}/history?source=Script", null, HttpStatusCode.OK)).GetProperty("totalCount").GetInt32());
        await ApiClient.ExpectAsync(factory, TestUsers.Bob, HttpMethod.Get, $"/api/documents/{documentId}/history?source=Other", null, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task The_admin_audit_log_filters_raw_rows_and_is_admin_only()
    {
        var (_, _, nodes) = await _arrange.SignedAsync();
        var ticket = $"INC-{Guid.NewGuid():N}"[..20];
        await using (var dbo = await SqlSession.OpenAsync(factory.AdminConnectionString))
        {
            await dbo.ExecuteAsync("EXEC audit.usp_SetSupportContext @Ticket = @t, @Reason = N'Fix'; UPDATE app.NodeContent SET ContentJson = @j WHERE NodeId = @n;",
                ("@t", ticket), ("@j", Paragraph("fixed")), ("@n", nodes[0]));
        }

        var now = DateTime.UtcNow;
        var range = $"from={Uri.EscapeDataString(now.AddDays(-1).ToString("o"))}&to={Uri.EscapeDataString(now.AddMinutes(5).ToString("o"))}";
        var rows = await ApiClient.ExpectAsync(factory, TestUsers.Admin, HttpMethod.Get, $"/api/admin/audit?{range}&source=Script&ticket={ticket}", null, HttpStatusCode.OK);
        var row = Assert.Single(rows.GetProperty("items").EnumerateArray());
        Assert.Equal(("app.NodeContent", "U", "Script"), (row.GetProperty("tableName").GetString(), row.GetProperty("operation").GetString(), row.GetProperty("source").GetString()));

        await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Get, $"/api/admin/audit?{range}", null, HttpStatusCode.Forbidden);
        await ApiClient.ExpectAsync(factory, TestUsers.Admin, HttpMethod.Get, "/api/admin/audit", null, HttpStatusCode.BadRequest);
        await ApiClient.ExpectAsync(factory, TestUsers.Admin, HttpMethod.Get,
            $"/api/admin/audit?from={Uri.EscapeDataString(now.AddDays(-40).ToString("o"))}&to={Uri.EscapeDataString(now.ToString("o"))}", null, HttpStatusCode.BadRequest);
        var tables = await ApiClient.ExpectAsync(factory, TestUsers.Admin, HttpMethod.Get, $"/api/admin/audit?{range}&table=app.Document&operation=I&pageSize=5", null, HttpStatusCode.OK);
        Assert.All(tables.GetProperty("items").EnumerateArray(), r => Assert.Equal("app.Document", r.GetProperty("tableName").GetString()));
    }

    [Fact]
    public async Task Change_summary_since_the_signed_version_covers_every_kind_of_node_in_one_call()
    {
        var (documentId, v1) = await _arrange.CreateAsync();
        var unchanged = await AddNodeAsync(v1, "Unchanged");
        var edited = await AddNodeAsync(v1, "Edited");
        var moved = await AddNodeAsync(v1, "Moved");
        var removed = await AddNodeAsync(v1, "Removed");
        await SaveAsync(removed, Paragraph("removed text"), TestUsers.Alice);
        var logical = new Dictionary<string, Guid>
        {
            ["Unchanged"] = await LogicalAsync(unchanged), ["Edited"] = await LogicalAsync(edited), ["Moved"] = await LogicalAsync(moved), ["Removed"] = await LogicalAsync(removed),
        };
        await _arrange.GrantAsync(documentId, TestUsers.Carol);
        await _arrange.SignAsync(v1, TestUsers.Carol);
        var draft = (await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Post, $"/api/documents/{documentId}/drafts", null, HttpStatusCode.Created)).GetProperty("id").GetInt32();

        await SaveAsync(await NodeOfAsync(draft, logical["Edited"]), Paragraph("new text"), TestUsers.Alice);
        var draftMoved = await NodeOfAsync(draft, logical["Moved"]);
        await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Post, $"/api/nodes/{draftMoved}/move",
            new { newParentNodeId = await NodeOfAsync(draft, logical["Unchanged"]), rowVersion = await NodeRowVersionAsync(draftMoved) }, HttpStatusCode.OK);
        await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Patch, $"/api/nodes/{draftMoved}", new { title = "Moved and renamed", rowVersion = await NodeRowVersionAsync(draftMoved) }, HttpStatusCode.OK);
        var draftRemoved = await NodeOfAsync(draft, logical["Removed"]);
        await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Delete, $"/api/nodes/{draftRemoved}?rowVersion={Uri.EscapeDataString(await NodeRowVersionAsync(draftRemoved))}", null, HttpStatusCode.OK);
        var added = await AddNodeAsync(draft, "Added");

        var summary = await ApiClient.ExpectAsync(factory, TestUsers.Bob, HttpMethod.Get, $"/api/versions/{draft}/change-summary?since=latestSigned", null, HttpStatusCode.OK);
        var nodes = summary.GetProperty("nodes").EnumerateArray().ToDictionary(n => n.GetProperty("logicalNodeId").GetGuid());
        JsonElement Node(Guid l) => nodes[l];
        List<string?> Kinds(Guid l) => Node(l).GetProperty("structural").EnumerateArray().Select(k => k.GetProperty("kind").GetString()).ToList();
        Assert.Equal(0, Node(logical["Unchanged"]).GetProperty("changeCount").GetInt32());
        Assert.Empty(Kinds(logical["Unchanged"]));
        Assert.Equal(1, Node(logical["Edited"]).GetProperty("changeCount").GetInt32());
        Assert.Equal(TestUsers.Alice, Node(logical["Edited"]).GetProperty("lastChangedBy").GetProperty("id").GetInt32());
        Assert.Empty(Kinds(logical["Edited"]));
        Assert.Equal(["Moved", "Renamed"], Kinds(logical["Moved"]));
        var move = Node(logical["Moved"]).GetProperty("structural")[0];
        Assert.Equal(("3", "1.1"), (move.GetProperty("oldNumber").GetString(), move.GetProperty("newNumber").GetString()));
        Assert.Equal("Moved and renamed", Node(logical["Moved"]).GetProperty("structural")[1].GetProperty("newTitle").GetString());
        Assert.Equal(["Added"], Kinds(await LogicalAsync(added)));

        var gone = Assert.Single(summary.GetProperty("removed").EnumerateArray());
        Assert.Equal((logical["Removed"], "Removed", "4", 3), (gone.GetProperty("logicalNodeId").GetGuid(), gone.GetProperty("title").GetString(), gone.GetProperty("number").GetString(), gone.GetProperty("formerPosition").GetInt32()));
        var placeholder = await ApiClient.ExpectAsync(factory, TestUsers.Bob, HttpMethod.Get, $"/api/history/entries/{gone.GetProperty("lastEntryId").GetInt64()}/content", null, HttpStatusCode.OK);
        Assert.Equal("<p>removed text</p>", placeholder.GetProperty("contentHtml").GetString());

        // since=e:{entryId}: only what came after that entry.
        var editEntry = (await HistoryAsync(documentId, logical["Edited"]))[0].GetProperty("id").GetInt64();
        var sinceEntry = await ApiClient.ExpectAsync(factory, TestUsers.Bob, HttpMethod.Get, $"/api/versions/{draft}/change-summary?since=e:{editEntry}", null, HttpStatusCode.OK);
        var sinceNodes = sinceEntry.GetProperty("nodes").EnumerateArray().ToDictionary(n => n.GetProperty("logicalNodeId").GetGuid());
        Assert.Equal(0, sinceNodes[logical["Edited"]].GetProperty("changeCount").GetInt32());
        Assert.Equal(2, sinceNodes[logical["Moved"]].GetProperty("changeCount").GetInt32());
        Assert.Contains("Moved", sinceNodes[logical["Moved"]].GetProperty("structural").EnumerateArray().Select(k => k.GetProperty("kind").GetString()));
        Assert.Single(sinceEntry.GetProperty("removed").EnumerateArray());

        await ApiClient.ExpectAsync(factory, TestUsers.Bob, HttpMethod.Get, $"/api/versions/{draft}/change-summary?since=bogus", null, HttpStatusCode.BadRequest);
        await ApiClient.ExpectAsync(factory, TestUsers.Bob, HttpMethod.Get, $"/api/versions/{draft}/change-summary?since=v:999999", null, HttpStatusCode.NotFound);
        Assert.Equal(1, (await ApiClient.ExpectAsync(factory, TestUsers.Bob, HttpMethod.Get, $"/api/versions/{draft}/change-summary?since=v:{v1}", null, HttpStatusCode.OK))
            .GetProperty("removed").GetArrayLength());
    }

    [Fact]
    public async Task Track_changes_attributes_every_run_to_its_author()
    {
        var (documentId, v1) = await _arrange.CreateAsync();
        var node = await AddNodeAsync(v1, "Scope");
        var logical = await LogicalAsync(node);
        await SaveAsync(node, Paragraph("The systm is fast."), TestUsers.Alice);
        await _arrange.GrantAsync(documentId, TestUsers.Carol);
        await _arrange.GrantAsync(documentId, TestUsers.Erin, role: 1);
        await _arrange.SignAsync(v1, TestUsers.Carol);
        var draft = (await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Post, $"/api/documents/{documentId}/drafts", null, HttpStatusCode.Created)).GetProperty("id").GetInt32();
        var draftNode = await NodeOfAsync(draft, logical);

        await SaveAsync(draftNode, Paragraph("The systm is fast. It stores every change."), TestUsers.Alice);
        await SaveAsync(draftNode, Paragraph("The systm is fast. It stores every change. Temporary."), TestUsers.Alice);
        await SaveAsync(draftNode, Paragraph("The systm is fast. It records every change."), TestUsers.Erin);
        await using (var dbo = await SqlSession.OpenAsync(factory.AdminConnectionString))
        {
            await dbo.ExecuteAsync("EXEC audit.usp_SetSupportContext @Ticket = N'INC-9', @Reason = N'Typo'; UPDATE app.NodeContent SET ContentJson = @j WHERE NodeId = @n;",
                ("@j", Paragraph("The system is fast. It records every change.")), ("@n", draftNode));
        }

        var changes = await ApiClient.ExpectAsync(factory, TestUsers.Bob, HttpMethod.Get, $"/api/documents/{documentId}/nodes/{logical}/changes?since=latestSigned", null, HttpStatusCode.OK);
        var ops = changes.GetProperty("blocks")[0].GetProperty("ops").EnumerateArray()
            .Where(o => o.GetProperty("op").GetString() != "equal")
            .Select(o => (Op: o.GetProperty("op").GetString(), Text: o.GetProperty("text").GetString()!, By: o.GetProperty("by")))
            .ToList();
        Assert.Contains(ops, o => o is { Op: "delete", Text: "systm" } && o.By.GetProperty("source").GetString() == "Script" && o.By.GetProperty("ticket").GetString() == "INC-9");
        Assert.Contains(ops, o => o is { Op: "insert", Text: "records" } && o.By.GetProperty("userId").GetInt32() == TestUsers.Erin);
        Assert.Contains(ops, o => o.Op == "insert" && o.Text.Contains("every change", StringComparison.Ordinal) && o.By.GetProperty("userId").GetInt32() == TestUsers.Alice);
        Assert.Equal("The system is fast. It records every change.", string.Concat(changes.GetProperty("blocks")[0].GetProperty("ops").EnumerateArray()
            .Where(o => o.GetProperty("op").GetString() != "delete").Select(o => o.GetProperty("text").GetString())));
        Assert.DoesNotContain(ops, o => o.Text.Contains("Temporary", StringComparison.Ordinal) || o.Text.Contains("stores", StringComparison.Ordinal));

        // Up to an entry: only Alice's first change.
        var first = (await HistoryAsync(documentId, logical)).Last(e => e.GetProperty("kind").GetString() == "ContentChanged" && e.GetProperty("versionId").GetInt32() == draft);
        var partial = await ApiClient.ExpectAsync(factory, TestUsers.Bob, HttpMethod.Get,
            $"/api/documents/{documentId}/nodes/{logical}/changes?since=latestSigned&until=e:{first.GetProperty("id").GetInt64()}", null, HttpStatusCode.OK);
        Assert.Equal(["insert"], partial.GetProperty("blocks")[0].GetProperty("ops").EnumerateArray().Select(o => o.GetProperty("op").GetString()).Where(o => o != "equal"));

        // Deleted document: hidden from others.
        await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Delete, $"/api/documents/{documentId}?rowVersion={Uri.EscapeDataString(await _arrange.RowVersionAsync(documentId))}", null, HttpStatusCode.NoContent);
        await ApiClient.ExpectAsync(factory, TestUsers.Bob, HttpMethod.Get, $"/api/documents/{documentId}/nodes/{logical}/changes", null, HttpStatusCode.NotFound);
        await ApiClient.ExpectAsync(factory, TestUsers.Bob, HttpMethod.Get, $"/api/versions/{draft}/change-summary", null, HttpStatusCode.NotFound);
        await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Get, $"/api/documents/{documentId}/nodes/{logical}/changes", null, HttpStatusCode.OK);
    }

    private async Task<string> NodeRowVersionAsync(int nodeId) =>
        (await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Get, $"/api/nodes/{nodeId}", null, HttpStatusCode.OK)).GetProperty("rowVersion").GetString()!;

    private async Task<List<JsonElement>> HistoryAsync(int documentId, Guid logicalNodeId) =>
        (await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Get, $"/api/documents/{documentId}/nodes/{logicalNodeId}/history?pageSize=200", null, HttpStatusCode.OK))
            .GetProperty("items").EnumerateArray().ToList();

    private async Task<int> AddNodeAsync(int versionId, string title) =>
        (await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Post, $"/api/versions/{versionId}/nodes", new { nodeTypeId = 1, title }, HttpStatusCode.Created))
            .GetProperty("id").GetInt32();

    private async Task<Guid> LogicalAsync(int nodeId) =>
        (await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Get, $"/api/nodes/{nodeId}", null, HttpStatusCode.OK)).GetProperty("logicalNodeId").GetGuid();

    private async Task<int> NodeOfAsync(int versionId, Guid logicalNodeId)
    {
        await using var dbo = await SqlSession.OpenAsync(factory.AdminConnectionString);
        return await dbo.ScalarAsync<int>("SELECT Id FROM app.DocumentNode WHERE DocumentVersionId = @v AND LogicalNodeId = @l;", ("@v", versionId), ("@l", logicalNodeId));
    }

    private async Task SaveAsync(int nodeId, string json, int userId)
    {
        var rowVersion = (await ApiClient.ExpectAsync(factory, TestUsers.Admin, HttpMethod.Get, $"/api/nodes/{nodeId}/content", null, HttpStatusCode.OK)).GetProperty("rowVersion").GetString();
        await ApiClient.ExpectAsync(factory, userId, HttpMethod.Put, $"/api/nodes/{nodeId}/content", new { contentJson = JsonNode.Parse(json), rowVersion }, HttpStatusCode.OK);
    }
}
