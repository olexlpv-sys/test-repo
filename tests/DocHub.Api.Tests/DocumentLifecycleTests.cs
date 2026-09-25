using System.Net;
using System.Text.Json;
using DocHub.Api.Documents;
using DocHub.Api.Tests.Infrastructure;
using DocHub.Testing.Database;
using Microsoft.Extensions.DependencyInjection;

namespace DocHub.Api.Tests;

/// <summary>T07: document and version lifecycle — drafts, all-approver signing, new drafts, deletion and restore.</summary>
public sealed class DocumentLifecycleTests(DocHubApiFactory factory) : IClassFixture<DocHubApiFactory>
{
    private readonly DocumentArrange _arrange = new(factory);

    [Fact]
    public async Task A_new_document_has_an_empty_draft_without_version_number_and_is_listed_as_draft()
    {
        var folder = (await ApiClient.ExpectAsync(factory, TestUsers.Admin, HttpMethod.Post, "/api/folders", new { name = $"Create {Guid.NewGuid():N}" }, HttpStatusCode.Created)).GetProperty("id").GetInt32();
        var (documentId, draftId) = await _arrange.CreateAsync(folderId: folder, title: "  Contract  ");

        var details = await _arrange.DocumentAsync(documentId);
        Assert.Equal("Contract", details.GetProperty("title").GetString());
        Assert.Equal("Draft", details.GetProperty("status").GetString());
        var version = Assert.Single(details.GetProperty("versions").EnumerateArray());
        Assert.Equal(draftId, version.GetProperty("id").GetInt32());
        Assert.Equal(JsonValueKind.Null, version.GetProperty("versionNumber").ValueKind);
        Assert.Equal("Draft", version.GetProperty("label").GetString());
        Assert.True(version.GetProperty("isCurrent").GetBoolean());
        Assert.True(details.GetProperty("myRoles").GetProperty("isOwner").GetBoolean());

        var list = await ApiClient.ExpectAsync(factory, TestUsers.Bob, HttpMethod.Get, $"/api/folders/{folder}/documents", null, HttpStatusCode.OK);
        var item = Assert.Single(list.GetProperty("items").EnumerateArray());
        Assert.Equal("Draft", item.GetProperty("status").GetString());
        Assert.True(item.GetProperty("hasDraft").GetBoolean());
        Assert.Equal(0, item.GetProperty("signatureProgress").GetProperty("required").GetInt32());
    }

    [Fact]
    public async Task All_approvers_must_sign_and_a_new_draft_is_an_identical_copy()
    {
        var (documentId, draftId) = await _arrange.CreateAsync();
        await _arrange.AddNodesAsync(draftId);
        await _arrange.GrantAsync(documentId, TestUsers.Carol);
        await _arrange.GrantAsync(documentId, TestUsers.Dave);

        var first = await _arrange.SignAsync(draftId, TestUsers.Carol);
        Assert.Equal("Draft", first.GetProperty("version").GetProperty("status").GetString());
        Assert.Equal([TestUsers.Dave], first.GetProperty("signatures").GetProperty("pendingApprovers").EnumerateArray().Select(u => u.GetProperty("id").GetInt32()));
        var list = await ApiClient.ExpectAsync(factory, TestUsers.Carol, HttpMethod.Get, "/api/folders/1/documents?pageSize=200", null, HttpStatusCode.OK);
        var progress = list.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetInt32() == documentId).GetProperty("signatureProgress");
        Assert.Equal((1, 2), (progress.GetProperty("signed").GetInt32(), progress.GetProperty("required").GetInt32()));

        var second = await _arrange.SignAsync(draftId, TestUsers.Dave);
        var v1 = second.GetProperty("version");
        Assert.Equal("Signed", v1.GetProperty("status").GetString());
        Assert.Equal(1, v1.GetProperty("versionNumber").GetInt32());
        Assert.Equal("v1", v1.GetProperty("label").GetString());
        Assert.Equal(2, v1.GetProperty("signedBy").GetArrayLength());
        Assert.True(second.GetProperty("signatures").GetProperty("isComplete").GetBoolean());

        var draft = await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Post, $"/api/documents/{documentId}/drafts", null, HttpStatusCode.Created);
        var draft2 = draft.GetProperty("id").GetInt32();
        Assert.Equal("Draft (based on v1)", draft.GetProperty("label").GetString());
        Assert.True(draft.GetProperty("isCurrent").GetBoolean());
        Assert.Equal(await TreeAsync(draftId), await TreeAsync(draft2));

        await _arrange.SignAsync(draft2, TestUsers.Carol);
        var v2 = (await _arrange.SignAsync(draft2, TestUsers.Dave)).GetProperty("version");
        Assert.Equal(2, v2.GetProperty("versionNumber").GetInt32());
        var versions = (await _arrange.DocumentAsync(documentId)).GetProperty("versions").EnumerateArray().ToList();
        Assert.Equal(["v1", "v2"], versions.Select(v => v.GetProperty("label").GetString()));
        Assert.Equal([false, true], versions.Select(v => v.GetProperty("isCurrent").GetBoolean()));
    }

    [Fact]
    public async Task A_content_change_outdates_signatures_until_they_are_given_again()
    {
        var (documentId, draftId) = await _arrange.CreateAsync();
        var nodes = await _arrange.AddNodesAsync(draftId);
        await _arrange.GrantAsync(documentId, TestUsers.Carol);
        await _arrange.GrantAsync(documentId, TestUsers.Dave);

        await _arrange.SignAsync(draftId, TestUsers.Carol);
        await _arrange.EditContentBySqlAsync(nodes[2]); // ContentJson only, not ContentHash
        var dave = await _arrange.SignAsync(draftId, TestUsers.Dave);
        Assert.Equal("Draft", dave.GetProperty("version").GetProperty("status").GetString());

        var status = await ApiClient.ExpectAsync(factory, TestUsers.Bob, HttpMethod.Get, $"/api/versions/{draftId}/signatures", null, HttpStatusCode.OK);
        var carol = status.GetProperty("signatures").EnumerateArray().Single(s => s.GetProperty("user").GetProperty("id").GetInt32() == TestUsers.Carol);
        Assert.False(carol.GetProperty("isValid").GetBoolean());
        Assert.Equal([TestUsers.Carol], status.GetProperty("pendingApprovers").EnumerateArray().Select(u => u.GetProperty("id").GetInt32()));
        var list = await ApiClient.ExpectAsync(factory, TestUsers.Carol, HttpMethod.Get, "/api/folders/1/documents?pageSize=200", null, HttpStatusCode.OK);
        Assert.Equal(1, list.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetInt32() == documentId).GetProperty("signatureProgress").GetProperty("signed").GetInt32());

        var again = await _arrange.SignAsync(draftId, TestUsers.Carol);
        Assert.Equal("Signed", again.GetProperty("version").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Revoking_an_approver_finalizes_when_the_others_have_signed_but_revoking_the_last_one_never_does()
    {
        var (documentId, draftId) = await _arrange.CreateAsync();
        await _arrange.AddNodesAsync(draftId);
        await _arrange.GrantAsync(documentId, TestUsers.Carol);
        await _arrange.GrantAsync(documentId, TestUsers.Dave);
        await _arrange.SignAsync(draftId, TestUsers.Carol);

        await _arrange.RevokeAsync(documentId, TestUsers.Dave);
        await RecheckFinalizationAsync(documentId);
        Assert.Equal("Signed", (await _arrange.VersionAsync(draftId)).GetProperty("status").GetString());

        var (only, onlyDraft) = await _arrange.CreateAsync();
        await _arrange.AddNodesAsync(onlyDraft);
        await _arrange.GrantAsync(only, TestUsers.Carol);
        await _arrange.RevokeAsync(only, TestUsers.Carol);
        await RecheckFinalizationAsync(only);
        Assert.Equal("Draft", (await _arrange.VersionAsync(onlyDraft)).GetProperty("status").GetString());
        var problem = await _arrange.SignAsync(onlyDraft, TestUsers.Carol, HttpStatusCode.Conflict);
        Assert.Equal("no-approvers", problem.GetProperty("type").GetString());
    }

    [Fact]
    public async Task Deleted_documents_reject_every_change_except_admin_move_and_restore()
    {
        var (documentId, versionId, _) = await _arrange.SignedAsync();
        var draft = (await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Post, $"/api/documents/{documentId}/drafts", null, HttpStatusCode.Created)).GetProperty("id").GetInt32();
        var draftRowVersion = Uri.EscapeDataString((await _arrange.VersionAsync(draft)).GetProperty("rowVersion").GetString()!);
        await _arrange.SignAsync(draft, TestUsers.Carol);
        await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Delete, $"/api/documents/{documentId}?rowVersion={Uri.EscapeDataString(await _arrange.RowVersionAsync(documentId))}", null, HttpStatusCode.NoContent);
        var rowVersion = await _arrange.RowVersionAsync(documentId);

        var attempts = new (int User, HttpMethod Method, string Path, object? Body)[]
        {
            (TestUsers.Alice, HttpMethod.Put, $"/api/documents/{documentId}", new { title = "x", rowVersion }),
            (TestUsers.Dave, HttpMethod.Post, $"/api/versions/{draft}/signatures", new { }),
            (TestUsers.Carol, HttpMethod.Delete, $"/api/versions/{draft}/signatures/mine", null),
            (TestUsers.Alice, HttpMethod.Delete, $"/api/versions/{draft}?rowVersion={draftRowVersion}", null),
            (TestUsers.Alice, HttpMethod.Post, $"/api/documents/{documentId}/drafts", null),
            (TestUsers.Alice, HttpMethod.Post, $"/api/documents/{documentId}/move", new { folderId = 2, rowVersion }),
        };
        foreach (var (user, method, path, body) in attempts)
        {
            var problem = await ApiClient.ExpectAsync(factory, user, method, path, body, user == TestUsers.Alice ? HttpStatusCode.Conflict : HttpStatusCode.NotFound);
            if (user == TestUsers.Alice)
            {
                Assert.Equal("document-deleted", problem.GetProperty("type").GetString());
            }
        }

        // Approvers can't see a deleted document at all (FR-P5) — 404 before any other rule.
        var moved = await ApiClient.ExpectAsync(factory, TestUsers.Admin, HttpMethod.Post, $"/api/documents/{documentId}/move", new { folderId = 2, rowVersion }, HttpStatusCode.OK);
        Assert.Equal(2, moved.GetProperty("folderId").GetInt32());
        var restored = await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Post, $"/api/documents/{documentId}/restore", new { folderId = 1 }, HttpStatusCode.OK);
        Assert.Equal("Draft", restored.GetProperty("status").GetString());
        Assert.Equal(1, restored.GetProperty("folderId").GetInt32());

        // Restored exactly as it was: the draft and carol's signature are unchanged, and it is listed again.
        var signatures = await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Get, $"/api/versions/{draft}/signatures", null, HttpStatusCode.OK);
        Assert.True(signatures.GetProperty("signatures").EnumerateArray().Single().GetProperty("isValid").GetBoolean());
        var list = await ApiClient.ExpectAsync(factory, TestUsers.Bob, HttpMethod.Get, "/api/folders/1/documents?pageSize=200", null, HttpStatusCode.OK);
        Assert.Contains(list.GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetInt32() == documentId);

        var notDeleted = await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Post, $"/api/documents/{documentId}/restore", new { }, HttpStatusCode.Conflict);
        Assert.Equal("not-deleted", notDeleted.GetProperty("type").GetString());

        // Non-deleted document: a Signed version can't be signed or discarded.
        var signedVersionRowVersion = Uri.EscapeDataString((await _arrange.VersionAsync(versionId)).GetProperty("rowVersion").GetString()!);
        Assert.Equal("version-not-editable", (await _arrange.SignAsync(versionId, TestUsers.Carol, HttpStatusCode.Conflict)).GetProperty("type").GetString());
        Assert.Equal("version-not-editable", (await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Delete, $"/api/versions/{versionId}?rowVersion={signedVersionRowVersion}", null, HttpStatusCode.Conflict)).GetProperty("type").GetString());
    }

    [Fact]
    public async Task Deleted_documents_are_visible_only_to_owner_and_admins()
    {
        var folder = (await ApiClient.ExpectAsync(factory, TestUsers.Admin, HttpMethod.Post, "/api/folders", new { name = $"Deleted {Guid.NewGuid():N}" }, HttpStatusCode.Created)).GetProperty("id").GetInt32();
        var (mine, _) = await _arrange.CreateAsync(TestUsers.Alice, folder);
        var (theirs, _) = await _arrange.CreateAsync(TestUsers.Bob, folder);
        foreach (var (id, owner) in new[] { (mine, TestUsers.Alice), (theirs, TestUsers.Bob) })
        {
            await ApiClient.ExpectAsync(factory, owner, HttpMethod.Delete, $"/api/documents/{id}?rowVersion={Uri.EscapeDataString(await _arrange.RowVersionAsync(id))}", null, HttpStatusCode.NoContent);
        }

        await _arrange.DocumentAsync(theirs, TestUsers.Carol, HttpStatusCode.NotFound);
        await _arrange.DocumentAsync(theirs, TestUsers.Alice, HttpStatusCode.NotFound);
        Assert.Equal("Deleted", (await _arrange.DocumentAsync(theirs, TestUsers.Admin)).GetProperty("status").GetString());
        Assert.Equal("Deleted", (await _arrange.DocumentAsync(mine, TestUsers.Alice)).GetProperty("status").GetString());

        var aliceList = await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Get, $"/api/folders/{folder}/documents?includeDeleted=true", null, HttpStatusCode.OK);
        Assert.Equal([mine], aliceList.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetInt32()));
        var adminList = await ApiClient.ExpectAsync(factory, TestUsers.Admin, HttpMethod.Get, $"/api/folders/{folder}/documents?includeDeleted=true", null, HttpStatusCode.OK);
        Assert.Equal(2, adminList.GetProperty("totalCount").GetInt32());
        var plain = await ApiClient.ExpectAsync(factory, TestUsers.Admin, HttpMethod.Get, $"/api/folders/{folder}/documents", null, HttpStatusCode.OK);
        Assert.Equal(0, plain.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task Draft_rules_second_draft_and_only_version()
    {
        var (documentId, draftId) = await _arrange.CreateAsync();
        // A new document already has its draft (and no signed version to copy from).
        Assert.Equal("draft-already-exists", (await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Post, $"/api/documents/{documentId}/drafts", null, HttpStatusCode.Conflict)).GetProperty("type").GetString());
        var draftRowVersion = Uri.EscapeDataString((await _arrange.VersionAsync(draftId)).GetProperty("rowVersion").GetString()!);
        Assert.Equal("only-version", (await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Delete, $"/api/versions/{draftId}?rowVersion={draftRowVersion}", null, HttpStatusCode.Conflict)).GetProperty("type").GetString());

        var (signedDoc, v1, _) = await _arrange.SignedAsync();
        var draft = (await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Post, $"/api/documents/{signedDoc}/drafts", null, HttpStatusCode.Created)).GetProperty("id").GetInt32();
        Assert.Equal("draft-already-exists", (await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Post, $"/api/documents/{signedDoc}/drafts", null, HttpStatusCode.Conflict)).GetProperty("type").GetString());

        // Discard: the draft becomes a discarded draft and v1 is current again.
        var rowVersion = Uri.EscapeDataString((await _arrange.VersionAsync(draft)).GetProperty("rowVersion").GetString()!);
        await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Delete, $"/api/versions/{draft}?rowVersion={rowVersion}", null, HttpStatusCode.NoContent);
        var versions = (await _arrange.DocumentAsync(signedDoc)).GetProperty("versions").EnumerateArray().ToList();
        Assert.Equal(["v1", "Discarded draft"], versions.Select(v => v.GetProperty("label").GetString()));
        Assert.True(versions.Single(v => v.GetProperty("id").GetInt32() == v1).GetProperty("isCurrent").GetBoolean());
        Assert.Equal("Signed", (await _arrange.DocumentAsync(signedDoc)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Only_the_owner_manages_and_only_approvers_sign()
    {
        var (documentId, v1, _) = await _arrange.SignedAsync();
        await _arrange.GrantAsync(documentId, TestUsers.Erin, role: 1); // editor
        var rowVersion = Uri.EscapeDataString(await _arrange.RowVersionAsync(documentId));

        await ApiClient.ExpectAsync(factory, TestUsers.Bob, HttpMethod.Delete, $"/api/documents/{documentId}?rowVersion={rowVersion}", null, HttpStatusCode.Forbidden);
        await ApiClient.ExpectAsync(factory, TestUsers.Carol, HttpMethod.Post, $"/api/documents/{documentId}/drafts", null, HttpStatusCode.Forbidden);
        await ApiClient.ExpectAsync(factory, TestUsers.Bob, HttpMethod.Put, $"/api/documents/{documentId}", new { title = "x", rowVersion = await _arrange.RowVersionAsync(documentId) }, HttpStatusCode.Forbidden);
        await ApiClient.ExpectAsync(factory, TestUsers.Bob, HttpMethod.Post, $"/api/documents/{documentId}/move", new { folderId = 2, rowVersion = await _arrange.RowVersionAsync(documentId) }, HttpStatusCode.Forbidden);

        var draft = (await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Post, $"/api/documents/{documentId}/drafts", null, HttpStatusCode.Created)).GetProperty("id").GetInt32();
        var draftRowVersion = Uri.EscapeDataString((await _arrange.VersionAsync(draft)).GetProperty("rowVersion").GetString()!);
        await ApiClient.ExpectAsync(factory, TestUsers.Carol, HttpMethod.Delete, $"/api/versions/{draft}?rowVersion={draftRowVersion}", null, HttpStatusCode.Forbidden);
        await _arrange.SignAsync(draft, TestUsers.Alice, HttpStatusCode.Forbidden); // owner
        await _arrange.SignAsync(draft, TestUsers.Erin, HttpStatusCode.Forbidden);  // editor
        await ApiClient.ExpectAsync(factory, TestUsers.Erin, HttpMethod.Delete, $"/api/versions/{draft}/signatures/mine", null, HttpStatusCode.Forbidden);

        // Withdraw: carol signs, withdraws, and her signature no longer counts.
        await _arrange.SignAsync(draft, TestUsers.Carol);
        var withdrawn = await ApiClient.ExpectAsync(factory, TestUsers.Carol, HttpMethod.Delete, $"/api/versions/{draft}/signatures/mine", null, HttpStatusCode.OK);
        Assert.Empty(withdrawn.GetProperty("signatures").GetProperty("signatures").EnumerateArray());
        await ApiClient.ExpectAsync(factory, TestUsers.Carol, HttpMethod.Delete, $"/api/versions/{draft}/signatures/mine", null, HttpStatusCode.NotFound);

        // The owner can rename; the title is document-level and v1 stays valid.
        var renamed = await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Put, $"/api/documents/{documentId}", new { title = "Renamed", rowVersion = await _arrange.RowVersionAsync(documentId) }, HttpStatusCode.OK);
        Assert.Equal("Renamed", renamed.GetProperty("title").GetString());
        var v1Status = await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Get, $"/api/versions/{v1}/signatures", null, HttpStatusCode.OK);
        Assert.All(v1Status.GetProperty("signatures").EnumerateArray(), s => Assert.True(s.GetProperty("isValid").GetBoolean()));
    }

    [Fact]
    public async Task Signing_empty_documents_is_rejected()
    {
        var (documentId, draftId) = await _arrange.CreateAsync();
        await _arrange.GrantAsync(documentId, TestUsers.Carol);

        await _arrange.SignAsync(draftId, TestUsers.Carol, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task The_version_header_revalidates_with_etag_and_shows_signing_immediately()
    {
        var (documentId, draftId) = await _arrange.CreateAsync();
        await _arrange.AddNodesAsync(draftId);
        await _arrange.GrantAsync(documentId, TestUsers.Carol);
        using var client = factory.CreateClientFor(TestUsers.Bob);
        using var first = await client.GetAsync(new Uri($"/api/versions/{draftId}", UriKind.Relative), TestContext.Current.CancellationToken);
        var etag = first.Headers.ETag!;
        Assert.True(first.Headers.CacheControl!.Private && first.Headers.CacheControl.NoCache);
        Assert.Contains("X-User-Id", first.Headers.Vary);

        using (var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"/api/versions/{draftId}", UriKind.Relative)))
        {
            request.Headers.IfNoneMatch.Add(etag);
            using var cached = await client.SendAsync(request, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NotModified, cached.StatusCode);
        }

        await _arrange.SignAsync(draftId, TestUsers.Carol);

        using (var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"/api/versions/{draftId}", UriKind.Relative)))
        {
            request.Headers.IfNoneMatch.Add(etag);
            using var fresh = await client.SendAsync(request, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
            var header = JsonDocument.Parse(await fresh.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;
            Assert.Equal("Signed", header.GetProperty("status").GetString());
        }
    }

    [Fact]
    public async Task Renaming_a_node_type_code_outdates_nothing_and_flags_nothing()
    {
        var (documentId, v1, _) = await _arrange.SignedAsync();
        var draft = (await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Post, $"/api/documents/{documentId}/drafts", null, HttpStatusCode.Created)).GetProperty("id").GetInt32();
        await _arrange.SignAsync(draft, TestUsers.Carol);
        var type = await ApiClient.ExpectAsync(factory, TestUsers.Admin, HttpMethod.Get, "/api/node-types/1", null, HttpStatusCode.OK);

        await ApiClient.ExpectAsync(factory, TestUsers.Admin, HttpMethod.Put, "/api/node-types/1",
            new { code = $"CHAPTER_{Random.Shared.Next(1000, 9999)}", name = "Chapter", sortOrder = 10, isActive = true, rowVersion = type.GetProperty("rowVersion").GetString() }, HttpStatusCode.OK);

        var status = await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Get, $"/api/versions/{draft}/signatures", null, HttpStatusCode.OK);
        Assert.True(status.GetProperty("signatures").EnumerateArray().Single().GetProperty("isValid").GetBoolean());
        Assert.False((await _arrange.VersionAsync(v1)).GetProperty("modifiedAfterSigning").GetBoolean());
    }

    [Fact]
    public async Task Modified_after_signing_follows_the_stamp_and_reconciliation_findings()
    {
        var (_, v1, nodes) = await _arrange.SignedAsync();
        Assert.False((await _arrange.VersionAsync(v1)).GetProperty("modifiedAfterSigning").GetBoolean());

        await _arrange.EditContentBySqlAsync(nodes[0]);
        Assert.True((await _arrange.VersionAsync(v1)).GetProperty("modifiedAfterSigning").GetBoolean());

        // A finding after signing keeps the flag even when the stamp is cleared.
        await using (var dbo = await SqlSession.OpenAsync(factory.AdminConnectionString))
        {
            await dbo.ExecuteAsync("INSERT INTO audit.ReconciliationFinding (Kind, TableName, EntityId, DocumentVersionId, LedgerTransactionId, AfterSigning) VALUES ('TriggerBypass', N'app.NodeContent', @n, @v, 1, 1);", ("@n", nodes[0]), ("@v", v1));
            await dbo.ExecuteAsync("UPDATE app.VersionStamp SET TamperedAt = NULL WHERE DocumentVersionId = @v;", ("@v", v1));
        }

        Assert.True((await _arrange.VersionAsync(v1)).GetProperty("modifiedAfterSigning").GetBoolean());

        // A draft never carries the flag; a finding recorded while it was a draft doesn't flag it after signing.
        var (documentId, draftId) = await _arrange.CreateAsync();
        var draftNodes = await _arrange.AddNodesAsync(draftId);
        await using (var dbo = await SqlSession.OpenAsync(factory.AdminConnectionString))
        {
            await dbo.ExecuteAsync("INSERT INTO audit.ReconciliationFinding (Kind, TableName, EntityId, DocumentVersionId, LedgerTransactionId, AfterSigning) VALUES ('TriggerBypass', N'app.NodeContent', @n, @v, 2, 0);", ("@n", draftNodes[0]), ("@v", draftId));
        }

        Assert.False((await _arrange.VersionAsync(draftId)).GetProperty("modifiedAfterSigning").GetBoolean());
        await _arrange.GrantAsync(documentId, TestUsers.Carol);
        await _arrange.SignAsync(draftId, TestUsers.Carol);
        Assert.False((await _arrange.VersionAsync(draftId)).GetProperty("modifiedAfterSigning").GetBoolean());
    }

    [Fact]
    public async Task Concurrent_last_signatures_assign_exactly_one_version_number()
    {
        for (var round = 0; round < 5; round++)
        {
            var (documentId, draftId) = await _arrange.CreateAsync();
            await _arrange.AddNodesAsync(draftId);
            await _arrange.GrantAsync(documentId, TestUsers.Carol);
            await _arrange.GrantAsync(documentId, TestUsers.Dave);

            var results = await Task.WhenAll(
                ApiClient.SendAsync(factory, TestUsers.Carol, HttpMethod.Post, $"/api/versions/{draftId}/signatures", new { }),
                ApiClient.SendAsync(factory, TestUsers.Dave, HttpMethod.Post, $"/api/versions/{draftId}/signatures", new { }));

            Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.Status));
            var versions = (await _arrange.DocumentAsync(documentId)).GetProperty("versions").EnumerateArray().ToList();
            var version = Assert.Single(versions);
            Assert.Equal("Signed", version.GetProperty("status").GetString());
            Assert.Equal(1, version.GetProperty("versionNumber").GetInt32());
            foreach (var r in results)
            {
                r.Response.Dispose();
            }
        }
    }

    [Fact]
    public async Task A_style_used_only_by_a_fresh_draft_cannot_be_deleted()
    {
        var style = await ApiClient.ExpectAsync(factory, TestUsers.Admin, HttpMethod.Post, "/api/content-styles",
            new { styleId = $"S{Guid.NewGuid():N}"[..20], name = "Draft only", kind = "Character", properties = new { bold = true } }, HttpStatusCode.Created);
        var styleId = style.GetProperty("styleId").GetString()!;
        var (documentId, v1, nodes) = await _arrange.SignedAsync();
        await using (var dbo = await SqlSession.OpenAsync(factory.AdminConnectionString))
        {
            await dbo.ExecuteAsync("INSERT INTO app.ContentStyleUsage (StyleId, NodeId) VALUES (@s, @n);", ("@s", styleId), ("@n", nodes[0]));
        }

        await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Post, $"/api/documents/{documentId}/drafts", null, HttpStatusCode.Created);
        await using (var dbo = await SqlSession.OpenAsync(factory.AdminConnectionString))
        {
            await dbo.ExecuteAsync("DELETE FROM app.ContentStyleUsage WHERE StyleId = @s AND NodeId = @n;", ("@s", styleId), ("@n", nodes[0]));
        }

        var problem = await ApiClient.ExpectAsync(factory, TestUsers.Admin, HttpMethod.Delete,
            $"/api/content-styles/{style.GetProperty("id").GetInt32()}?rowVersion={Uri.EscapeDataString(style.GetProperty("rowVersion").GetString()!)}", null, HttpStatusCode.Conflict);
        Assert.Equal("in-use", problem.GetProperty("type").GetString());
        Assert.NotEqual(0, v1);
    }

    private async Task RecheckFinalizationAsync(int documentId)
    {
        // T10's revoke endpoint does exactly this after deleting the grant.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocHub.Infrastructure.Persistence.DocHubDbContext>();
        var signing = scope.ServiceProvider.GetRequiredService<SigningService>();
        await DocHub.Infrastructure.Persistence.TransactionExtensions.InTransactionAsync(db, async () =>
        {
            await DocHub.Infrastructure.Persistence.TransactionExtensions.LockAsync(db, SigningService.LockResource(documentId), TestContext.Current.CancellationToken);
            await signing.FinalizeDraftIfCompleteAsync(documentId, TestContext.Current.CancellationToken);
            return true;
        }, TestContext.Current.CancellationToken);
    }

    private async Task<List<string>> TreeAsync(int versionId) =>
        (await Db.QueryAsync(factory,
            """
            SELECT CONCAT(n.LogicalNodeId, '|', p.LogicalNodeId, '|', n.NodeTypeId, '|', n.Title, '|', n.SortOrder, '|', c.ContentJson, '|', CONVERT(VARCHAR (64), c.ContentHash, 2)) AS Row
            FROM app.DocumentNode n LEFT JOIN app.DocumentNode p ON p.Id = n.ParentNodeId JOIN app.NodeContent c ON c.NodeId = n.Id
            WHERE n.DocumentVersionId = @v ORDER BY n.LogicalNodeId;
            """, ("@v", versionId)))
        .Select(r => (string)r["Row"]!)
        .ToList();
}
