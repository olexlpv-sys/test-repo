using DocHub.Testing.Database;
using static DocHub.Testing.Database.TestData;

namespace DocHub.Database.Tests;

/// <summary>Business rules enforced by the schema (T02 acceptance criteria 3–5 and the other constraints of the data model).</summary>
public sealed class SchemaConstraintTests(DocHubDatabaseFixture database) : IClassFixture<DocHubDatabaseFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Second_draft_for_the_same_document_is_rejected()
    {
        await using var db = await database.OpenRolledBackTransactionAsync(Ct);
        var documentId = await db.Data.DocumentAsync();
        await db.Data.VersionAsync(documentId, StatusDraft);

        await SqlAssert.ViolatesAsync("UX_DocumentVersion_OneDraft", () => db.Data.VersionAsync(documentId, StatusDraft));
    }

    [Fact]
    public async Task Draft_is_allowed_again_after_the_previous_draft_was_signed_or_discarded()
    {
        await using var db = await database.OpenRolledBackTransactionAsync(Ct);
        var documentId = await db.Data.DocumentAsync();
        await db.Data.VersionAsync(documentId, StatusSigned, versionNumber: 1);
        await db.Data.VersionAsync(documentId, StatusDeleted);

        var draftId = await db.Data.VersionAsync(documentId, StatusDraft);

        Assert.True(draftId > 0);
    }

    [Fact]
    public async Task Node_whose_parent_belongs_to_another_version_is_rejected()
    {
        await using var db = await database.OpenRolledBackTransactionAsync(Ct);
        var documentId = await db.Data.DocumentAsync();
        var v1 = await db.Data.VersionAsync(documentId, StatusSigned, versionNumber: 1);
        var draft = await db.Data.VersionAsync(documentId, StatusDraft);
        var parentInV1 = await db.Data.NodeAsync(v1);

        await SqlAssert.ViolatesAsync("FK_DocumentNode_Parent", () => db.Data.NodeAsync(draft, parentNodeId: parentInV1));
    }

    [Fact]
    public async Task Nodes_can_nest_to_arbitrary_depth_within_one_version()
    {
        await using var db = await database.OpenRolledBackTransactionAsync(Ct);
        var versionId = await db.Data.VersionAsync(await db.Data.DocumentAsync());

        int? parent = null;
        for (var depth = 0; depth < 20; depth++)
        {
            parent = await db.Data.NodeAsync(versionId, parentNodeId: parent, title: $"Level {depth}");
        }

        Assert.Equal(20, await db.ScalarAsync<int>("SELECT COUNT(*) FROM app.DocumentNode WHERE DocumentVersionId = @v", ("@v", versionId)));
    }

    [Fact]
    public async Task Signed_status_requires_version_number_and_signing_time()
    {
        await using var db = await database.OpenRolledBackTransactionAsync(Ct);
        var documentId = await db.Data.DocumentAsync();

        await SqlAssert.ViolatesAsync("CK_DocumentVersion_Signed", () => db.ExecuteAsync(
            "INSERT INTO app.DocumentVersion (DocumentId, Status, CreatedByUserId) VALUES (@d, 2, @u);",
            ("@d", documentId), ("@u", AliceUserId)));
    }

    [Fact]
    public async Task Draft_cannot_carry_a_version_number()
    {
        await using var db = await database.OpenRolledBackTransactionAsync(Ct);
        var documentId = await db.Data.DocumentAsync();

        await SqlAssert.ViolatesAsync("CK_DocumentVersion_Signed", () => db.Data.VersionAsync(documentId, StatusDraft, versionNumber: 1));
    }

    [Fact]
    public async Task Version_numbers_are_unique_per_document()
    {
        await using var db = await database.OpenRolledBackTransactionAsync(Ct);
        var documentId = await db.Data.DocumentAsync();
        await db.Data.VersionAsync(documentId, StatusSigned, versionNumber: 1);

        await SqlAssert.ViolatesAsync("UX_DocumentVersion_VersionNumber", () => db.Data.VersionAsync(documentId, StatusSigned, versionNumber: 1));
    }

    [Fact]
    public async Task Only_one_current_version_per_document()
    {
        await using var db = await database.OpenRolledBackTransactionAsync(Ct);
        var documentId = await db.Data.DocumentAsync();
        await db.Data.VersionAsync(documentId, StatusSigned, versionNumber: 1, isCurrent: true);

        await SqlAssert.ViolatesAsync("UX_DocumentVersion_Current", () => db.Data.VersionAsync(documentId, StatusDraft, isCurrent: true));
    }

    [Fact]
    public async Task Discarded_draft_cannot_be_current()
    {
        await using var db = await database.OpenRolledBackTransactionAsync(Ct);
        var documentId = await db.Data.DocumentAsync();

        await SqlAssert.ViolatesAsync("CK_DocumentVersion_DeletedNotCurrent", () => db.Data.VersionAsync(documentId, StatusDeleted, isCurrent: true));
    }

    [Fact]
    public async Task Node_scoped_grant_is_only_allowed_for_editors()
    {
        await using var db = await database.OpenRolledBackTransactionAsync(Ct);
        var documentId = await db.Data.DocumentAsync();

        await SqlAssert.ViolatesAsync("CK_DocumentPermission_NodeScopeOnlyEditor", () => db.ExecuteAsync(
            "INSERT INTO app.DocumentPermission (DocumentId, UserId, Role, LogicalNodeId, GrantedByUserId) VALUES (@d, @u, 2, NEWID(), @o);",
            ("@d", documentId), ("@u", CarolUserId), ("@o", AliceUserId)));
    }

    [Fact]
    public async Task Duplicate_document_level_grant_is_rejected()
    {
        await using var db = await database.OpenRolledBackTransactionAsync(Ct);
        var documentId = await db.Data.DocumentAsync();
        const string grant = "INSERT INTO app.DocumentPermission (DocumentId, UserId, Role, GrantedByUserId) VALUES (@d, @u, 2, @o);";
        await db.ExecuteAsync(grant, ("@d", documentId), ("@u", CarolUserId), ("@o", AliceUserId));

        await SqlAssert.ViolatesAsync("UX_DocumentPermission_Document", () => db.ExecuteAsync(grant, ("@d", documentId), ("@u", CarolUserId), ("@o", AliceUserId)));
    }

    [Theory]
    [InlineData(null, "UX_Folder_Root_Name")]
    [InlineData(1, "UX_Folder_Parent_Name")]
    public async Task Folder_names_are_unique_among_siblings_ignoring_case(int? parentFolderId, string index)
    {
        await using var db = await database.OpenRolledBackTransactionAsync(Ct);
        const string insert = "INSERT INTO app.Folder (ParentFolderId, Name, SortOrder, CreatedByUserId) VALUES (@p, @n, 1, 1);";
        await db.ExecuteAsync(insert, ("@p", parentFolderId), ("@n", "Contracts"));

        await SqlAssert.ViolatesAsync(index, () => db.ExecuteAsync(insert, ("@p", parentFolderId), ("@n", "CONTRACTS")));
    }

    [Fact]
    public async Task Same_folder_name_is_allowed_under_different_parents()
    {
        await using var db = await database.OpenRolledBackTransactionAsync(Ct);
        const string insert = "INSERT INTO app.Folder (ParentFolderId, Name, SortOrder, CreatedByUserId) VALUES (@p, N'Contracts', 1, 1);";

        await db.ExecuteAsync(insert, ("@p", 1));
        await db.ExecuteAsync(insert, ("@p", 2));
        await db.ExecuteAsync(insert, ("@p", null));

        Assert.Equal(3, await db.ScalarAsync<int>("SELECT COUNT(*) FROM app.Folder WHERE Name = N'Contracts'"));
    }

    [Fact]
    public async Task Node_content_must_be_valid_json()
    {
        await using var db = await database.OpenRolledBackTransactionAsync(Ct);
        var nodeId = await db.Data.NodeAsync(await db.Data.VersionAsync(await db.Data.DocumentAsync()));

        await SqlAssert.ViolatesAsync("CK_NodeContent_ContentJson", () => db.Data.ContentAsync(nodeId, "<p>not json</p>"));
    }

    [Fact]
    public async Task Node_content_must_match_the_node_version_and_logical_id()
    {
        await using var db = await database.OpenRolledBackTransactionAsync(Ct);
        var documentId = await db.Data.DocumentAsync();
        var nodeId = await db.Data.NodeAsync(await db.Data.VersionAsync(documentId));

        await SqlAssert.ViolatesAsync("FK_NodeContent_Node", () => db.ExecuteAsync(
            """
            INSERT INTO app.NodeContent (NodeId, DocumentVersionId, LogicalNodeId, ContentHash, ModifiedByUserId)
            SELECT n.Id, n.DocumentVersionId, NEWID(), 0x0000000000000000000000000000000000000000000000000000000000000000, 2
            FROM app.DocumentNode AS n WHERE n.Id = @n;
            """, ("@n", nodeId)));
    }

    [Fact]
    public async Task Deleting_a_node_cascades_to_its_content_and_style_usage()
    {
        await using var db = await database.OpenRolledBackTransactionAsync(Ct);
        var nodeId = await db.Data.NodeAsync(await db.Data.VersionAsync(await db.Data.DocumentAsync()));
        await db.Data.ContentAsync(nodeId);
        await db.ExecuteAsync("INSERT INTO app.ContentStyleUsage (StyleId, NodeId) VALUES ('Heading1', @n);", ("@n", nodeId));

        await db.ExecuteAsync("DELETE FROM app.DocumentNode WHERE Id = @n;", ("@n", nodeId));

        Assert.Equal(0, await db.ScalarAsync<int>("SELECT COUNT(*) FROM app.NodeContent WHERE NodeId = @n", ("@n", nodeId)));
        Assert.Equal(0, await db.ScalarAsync<int>("SELECT COUNT(*) FROM app.ContentStyleUsage WHERE NodeId = @n", ("@n", nodeId)));
    }

    [Fact]
    public async Task Style_used_by_content_cannot_be_deleted()
    {
        await using var db = await database.OpenRolledBackTransactionAsync(Ct);
        var nodeId = await db.Data.NodeAsync(await db.Data.VersionAsync(await db.Data.DocumentAsync()));
        await db.Data.ContentAsync(nodeId);
        await db.ExecuteAsync("INSERT INTO app.ContentStyleUsage (StyleId, NodeId) VALUES ('Quote', @n);", ("@n", nodeId));

        await SqlAssert.ViolatesAsync("FK_ContentStyleUsage_Style", () => db.ExecuteAsync("DELETE FROM app.ContentStyle WHERE StyleId = 'Quote';"));
    }

    [Fact]
    public async Task Approver_has_at_most_one_active_signature_per_version()
    {
        await using var db = await database.OpenRolledBackTransactionAsync(Ct);
        var versionId = await db.Data.VersionAsync(await db.Data.DocumentAsync());
        const string sign = "INSERT INTO app.VersionSignature (DocumentVersionId, UserId, ContentHash) VALUES (@v, @u, 0x0000000000000000000000000000000000000000000000000000000000000000);";
        await db.ExecuteAsync(sign, ("@v", versionId), ("@u", CarolUserId));

        await SqlAssert.ViolatesAsync("UX_VersionSignature_Active", () => db.ExecuteAsync(sign, ("@v", versionId), ("@u", CarolUserId)));

        await db.ExecuteAsync("UPDATE app.VersionSignature SET WithdrawnAt = SYSUTCDATETIME() WHERE DocumentVersionId = @v;", ("@v", versionId));
        await db.ExecuteAsync(sign, ("@v", versionId), ("@u", CarolUserId));
    }

    [Fact]
    public async Task Soft_delete_requires_both_time_and_user()
    {
        await using var db = await database.OpenRolledBackTransactionAsync(Ct);
        var documentId = await db.Data.DocumentAsync();

        await SqlAssert.ViolatesAsync("CK_Document_Deleted", () => db.ExecuteAsync(
            "UPDATE app.Document SET DeletedAt = SYSUTCDATETIME() WHERE Id = @d;", ("@d", documentId)));
    }
}
