using DocHub.Testing.Database;
using Microsoft.Data.SqlClient;
using static DocHub.Testing.Database.TestData;

namespace DocHub.Database.Tests;

/// <summary>Database-level change tracking (T03, FR-H1/H3/H5): every change is audited — by the API or by support scripts.</summary>
public sealed class AuditTriggerTests(DocHubDatabaseFixture database) : IClassFixture<DocHubDatabaseFixture>
{
    private const string NewJson = """{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"Changed"}]}]}""";
    private const int PermissionDenied = 229;

    private static readonly string[] AuditedTables =
    [
        "app.Comment", "app.ContentStyle", "app.Document", "app.DocumentNode", "app.DocumentPermission", "app.DocumentVersion",
        "app.Folder", "app.NodeContent", "app.NodeType", "app.User", "app.VersionSignature",
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_tracked_table_has_an_audit_trigger()
    {
        await using var db = await database.OpenRolledBackTransactionAsync(Ct);

        var rows = await db.QueryAsync(
            """
            SELECT s.name + '.' + t.name AS TableName
            FROM sys.triggers AS tr JOIN sys.tables AS t ON t.object_id = tr.parent_id JOIN sys.schemas AS s ON s.schema_id = t.schema_id
            WHERE tr.name LIKE 'TR[_]%[_]Audit' AND tr.is_disabled = 0
            ORDER BY 1;
            """);

        Assert.Equal(AuditedTables.Order(StringComparer.Ordinal), rows.Select(r => (string)r["TableName"]!));
    }

    [Fact]
    public async Task Api_change_is_recorded_as_App_with_user_correlation_and_changed_columns()
    {
        await using var db = await database.OpenRolledBackTransactionAsync(Ct);
        var (_, nodeId) = await ContentNodeAsync(db);
        var api = await db.Data.DatabaseUserInRoleAsync("app_api");

        await AsUserAsync(db, api, """
            EXEC sp_set_session_context N'UserId', 2;
            EXEC sp_set_session_context N'CorrelationId', N'req-42';
            """);
        await db.ExecuteAsync("UPDATE app.NodeContent SET ContentJson = @j WHERE NodeId = @n;", ("@j", NewJson), ("@n", nodeId));
        await db.ExecuteAsync("REVERT;");

        var row = Assert.Single(await ChangesAsync(db, "app.NodeContent", nodeId, "U"));
        Assert.Equal("App", row["Source"]);
        Assert.Equal(AliceUserId, row["UserId"]);
        Assert.Equal("req-42", row["CorrelationId"]);
        Assert.Equal("ContentJson", row["ChangedColumns"]);
        Assert.Contains("\\\"Changed\\\"", (string)row["NewValues"]!, StringComparison.Ordinal);
        Assert.Contains("ContentJson", (string)row["OldValues"]!, StringComparison.Ordinal);
        Assert.DoesNotContain("ContentHtml", (string)row["NewValues"]!, StringComparison.Ordinal);
        Assert.DoesNotContain("PlainText", (string)row["NewValues"]!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Script_change_without_context_is_recorded_as_Script_with_the_database_login()
    {
        await using var db = await database.OpenRolledBackTransactionAsync(Ct);
        var (_, nodeId) = await ContentNodeAsync(db);
        var support = await db.Data.DatabaseUserInRoleAsync("support_writer");
        var login = await db.ScalarAsync<string>("SELECT ORIGINAL_LOGIN();");

        await AsUserAsync(db, support);
        await db.ExecuteAsync("UPDATE app.NodeContent SET ContentJson = @j WHERE NodeId = @n;", ("@j", NewJson), ("@n", nodeId));
        await db.ExecuteAsync("REVERT;");

        var row = Assert.Single(await ChangesAsync(db, "app.NodeContent", nodeId, "U"));
        Assert.Equal("Script", row["Source"]);
        Assert.Null(row["UserId"]);
        Assert.Equal(login, row["DbLogin"]);
        Assert.Null(row["Ticket"]);
    }

    [Fact]
    public async Task Support_script_cannot_pose_as_the_api_by_setting_UserId()
    {
        await using var db = await database.OpenRolledBackTransactionAsync(Ct);
        var (_, nodeId) = await ContentNodeAsync(db);
        var support = await db.Data.DatabaseUserInRoleAsync("support_writer");

        await AsUserAsync(db, support, "EXEC sp_set_session_context N'UserId', 2;");
        await db.ExecuteAsync("UPDATE app.NodeContent SET ContentJson = @j WHERE NodeId = @n;", ("@j", NewJson), ("@n", nodeId));
        await db.ExecuteAsync("REVERT;");

        var row = Assert.Single(await ChangesAsync(db, "app.NodeContent", nodeId, "U"));
        Assert.Equal("Script", row["Source"]);
        Assert.Equal(AliceUserId, row["UserId"]);
    }

    [Fact]
    public async Task Support_context_adds_ticket_reason_and_acting_user()
    {
        await using var db = await database.OpenRolledBackTransactionAsync(Ct);
        var (_, nodeId) = await ContentNodeAsync(db);
        var support = await db.Data.DatabaseUserInRoleAsync("support_writer");

        await AsUserAsync(db, support, "EXEC audit.usp_SetSupportContext @Ticket = N' INC-1234 ', @Reason = N'Fix typo', @ActingUserId = 3;");
        await db.ExecuteAsync("UPDATE app.NodeContent SET ContentJson = @j WHERE NodeId = @n;", ("@j", NewJson), ("@n", nodeId));
        await db.ExecuteAsync("REVERT;");

        var row = Assert.Single(await ChangesAsync(db, "app.NodeContent", nodeId, "U"));
        Assert.Equal("Script", row["Source"]);
        Assert.Equal("INC-1234", row["Ticket"]);
        Assert.Equal("Fix typo", row["Reason"]);
        Assert.Equal(BobUserId, row["UserId"]);
    }

    [Theory]
    [InlineData("", "reason", null, 50001)]
    [InlineData("INC-1", " ", null, 50002)]
    [InlineData("INC-1", "reason", 999999, 50003)]
    public async Task Support_context_rejects_missing_ticket_reason_or_unknown_user(string ticket, string reason, int? actingUserId, int expectedError)
    {
        await using var db = await database.OpenRolledBackTransactionAsync(Ct);

        var exception = await Assert.ThrowsAsync<SqlException>(() => db.ExecuteAsync(
            "EXEC audit.usp_SetSupportContext @Ticket = @t, @Reason = @r, @ActingUserId = @u;", ("@t", ticket), ("@r", reason), ("@u", actingUserId)));

        Assert.Equal(expectedError, exception.Number);
    }

    [Fact]
    public async Task Malformed_session_context_never_blocks_a_change()
    {
        await using var db = await database.OpenRolledBackTransactionAsync(Ct);
        var (_, nodeId) = await ContentNodeAsync(db);

        await db.ExecuteAsync("EXEC sp_set_session_context N'UserId', N'not-a-number';");
        await db.ExecuteAsync("UPDATE app.NodeContent SET ContentJson = @j WHERE NodeId = @n;", ("@j", NewJson), ("@n", nodeId));

        var row = Assert.Single(await ChangesAsync(db, "app.NodeContent", nodeId, "U"));
        Assert.Equal("Script", row["Source"]);
        Assert.Null(row["UserId"]);
    }

    [Fact]
    public async Task Multi_row_update_writes_one_row_per_changed_row_and_no_op_updates_write_none()
    {
        await using var db = await database.OpenRolledBackTransactionAsync(Ct);
        var versionId = await db.Data.VersionAsync(await db.Data.DocumentAsync());
        await db.ExecuteAsync(
            """
            INSERT INTO app.DocumentNode (DocumentVersionId, LogicalNodeId, NodeTypeId, Title, SortOrder, CreatedByUserId, ModifiedByUserId)
            SELECT TOP (500) @v, NEWID(), 1, N'Node', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)), 2, 2
            FROM sys.all_objects AS a CROSS JOIN sys.all_objects AS b;
            """, ("@v", versionId));

        await db.ExecuteAsync("UPDATE app.DocumentNode SET Title = N'Renamed' WHERE DocumentVersionId = @v;", ("@v", versionId));
        Assert.Equal(500, await CountAsync(db, "app.DocumentNode", versionId, "U"));

        await db.ExecuteAsync("UPDATE app.DocumentNode SET Title = N'Renamed', SortOrder = SortOrder WHERE DocumentVersionId = @v;", ("@v", versionId));
        Assert.Equal(500, await CountAsync(db, "app.DocumentNode", versionId, "U"));
    }

    [Fact]
    public async Task Case_only_and_trailing_space_changes_are_audited()
    {
        await using var db = await database.OpenRolledBackTransactionAsync(Ct);
        var nodeId = await db.Data.NodeAsync(await db.Data.VersionAsync(await db.Data.DocumentAsync()), title: "scope");

        await db.ExecuteAsync("UPDATE app.DocumentNode SET Title = N'Scope' WHERE Id = @n;", ("@n", nodeId));
        await db.ExecuteAsync("UPDATE app.DocumentNode SET Title = N'Scope ' WHERE Id = @n;", ("@n", nodeId));

        var rows = await ChangesAsync(db, "app.DocumentNode", nodeId, "U");
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal("Title", r["ChangedColumns"]));
    }

    [Fact]
    public async Task Deleting_a_node_logs_node_and_cascaded_content_with_version_and_logical_ids()
    {
        await using var db = await database.OpenRolledBackTransactionAsync(Ct);
        var (versionId, nodeId) = await ContentNodeAsync(db);
        var logicalNodeId = await db.ScalarAsync<Guid>("SELECT LogicalNodeId FROM app.DocumentNode WHERE Id = @n", ("@n", nodeId));
        var documentId = await db.ScalarAsync<int>("SELECT DocumentId FROM app.DocumentVersion WHERE Id = @v", ("@v", versionId));

        await db.ExecuteAsync("DELETE FROM app.DocumentNode WHERE Id = @n;", ("@n", nodeId));

        foreach (var table in new[] { "app.DocumentNode", "app.NodeContent" })
        {
            var row = Assert.Single(await ChangesAsync(db, table, nodeId, "D"));
            Assert.Equal(versionId, row["DocumentVersionId"]);
            Assert.Equal(logicalNodeId, row["LogicalNodeId"]);
            Assert.Equal(documentId, row["DocumentId"]);
            Assert.NotNull(row["OldValues"]);
            Assert.Null(row["NewValues"]);
        }
    }

    [Fact]
    public async Task Inserts_resolve_the_document_version_and_node_of_the_changed_row()
    {
        await using var db = await database.OpenRolledBackTransactionAsync(Ct);
        var documentId = await db.Data.DocumentAsync();
        var versionId = await db.Data.VersionAsync(documentId);
        var logicalNodeId = Guid.NewGuid();
        await db.Data.NodeAsync(versionId, logicalNodeId: logicalNodeId);
        await db.ExecuteAsync(
            """
            INSERT INTO app.DocumentPermission (DocumentId, UserId, Role, LogicalNodeId, GrantedByUserId) VALUES (@d, 3, 1, @l, 2);
            INSERT INTO app.Comment (DocumentId, DocumentVersionId, LogicalNodeId, AuthorUserId, Body) VALUES (@d, @v, @l, 4, N'Looks good');
            INSERT INTO app.VersionSignature (DocumentVersionId, UserId, ContentHash) VALUES (@v, 4, 0x0000000000000000000000000000000000000000000000000000000000000000);
            """, ("@d", documentId), ("@v", versionId), ("@l", logicalNodeId));

        var rows = await db.QueryAsync(
            "SELECT TableName, DocumentId, DocumentVersionId, LogicalNodeId FROM audit.ChangeLog WHERE Operation = 'I' AND DocumentId = @d;",
            ("@d", documentId));

        Assert.Equal(documentId, Row(rows, "app.Document")["DocumentId"]);
        Assert.Equal(versionId, Row(rows, "app.DocumentVersion")["DocumentVersionId"]);
        Assert.Equal(logicalNodeId, Row(rows, "app.DocumentNode")["LogicalNodeId"]);
        Assert.Equal(logicalNodeId, Row(rows, "app.DocumentPermission")["LogicalNodeId"]);
        Assert.Equal(logicalNodeId, Row(rows, "app.Comment")["LogicalNodeId"]);
        Assert.Equal(versionId, Row(rows, "app.VersionSignature")["DocumentVersionId"]);
    }

    [Theory]
    [InlineData("app_api", "UPDATE audit.ChangeLog SET Reason = N'x';")]
    [InlineData("app_api", "DELETE FROM audit.ChangeLog;")]
    [InlineData("support_writer", "UPDATE audit.ChangeLog SET Reason = N'x';")]
    [InlineData("support_writer", "DELETE FROM audit.ChangeLog;")]
    public async Task Change_log_is_append_only_for_non_dbo_users(string role, string sql)
    {
        await using var db = await database.OpenRolledBackTransactionAsync(Ct);
        await db.Data.DocumentAsync();
        var user = await db.Data.DatabaseUserInRoleAsync(role);
        await AsUserAsync(db, user);

        var exception = await Assert.ThrowsAsync<SqlException>(() => db.ExecuteAsync(sql));

        Assert.Equal(PermissionDenied, exception.Number);
    }

    [Fact]
    public async Task Support_template_runs_as_support_writer_and_records_ticket_and_reason()
    {
        await using var db = await database.OpenRolledBackTransactionAsync(Ct);
        var (_, nodeId) = await ContentNodeAsync(db);
        var support = await db.Data.DatabaseUserInRoleAsync("support_writer");

        await AsUserAsync(db, support);
        await db.ExecuteAsync(SupportTemplate(nodeId));
        await db.ExecuteAsync("REVERT;");

        var row = Assert.Single(await ChangesAsync(db, "app.NodeContent", nodeId, "U"));
        Assert.Equal("Script", row["Source"]);
        Assert.Equal("INC-7", row["Ticket"]);
        Assert.Equal("Template test", row["Reason"]);
    }

    [Fact]
    public async Task Support_template_fails_for_readonly_users()
    {
        await using var db = await database.OpenRolledBackTransactionAsync(Ct);
        var (_, nodeId) = await ContentNodeAsync(db);
        var reader = await db.Data.DatabaseUserInRoleAsync("readonly");
        await AsUserAsync(db, reader);

        var exception = await Assert.ThrowsAsync<SqlException>(() => db.ExecuteAsync(SupportTemplate(nodeId)));

        Assert.Equal(PermissionDenied, exception.Number);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("RebuildDerived", false)]
    [InlineData("ApiContentSave", false)]
    [InlineData(null, true)]
    public async Task Script_edit_of_signed_content_is_audited_flagged_stale_and_marks_tampering(string? spoofedOperationContext, bool withSupportContext)
    {
        await using var db = await database.OpenRolledBackTransactionAsync(Ct);
        var (versionId, nodeId) = await ContentNodeAsync(db, VersionState.Signed);
        var support = await db.Data.DatabaseUserInRoleAsync("support_writer");

        await AsUserAsync(db, support);
        if (withSupportContext)
        {
            await db.ExecuteAsync("EXEC audit.usp_SetSupportContext @Ticket = N'INC-9', @Reason = N'Typo', @ActingUserId = 2;");
        }

        await db.ExecuteAsync("EXEC sp_set_session_context N'OperationContext', @c;", ("@c", spoofedOperationContext));
        await db.ExecuteAsync("UPDATE app.NodeContent SET ContentJson = @j WHERE NodeId = @n;", ("@j", NewJson), ("@n", nodeId));
        await db.ExecuteAsync("REVERT;");

        var row = Assert.Single(await ChangesAsync(db, "app.NodeContent", nodeId, "U"));
        Assert.Equal("Script", row["Source"]);
        Assert.True(await db.ScalarAsync<bool>("SELECT DerivedStale FROM app.NodeContent WHERE NodeId = @n", ("@n", nodeId)));
        Assert.Equal(1, await db.ScalarAsync<int>("SELECT COUNT(*) FROM app.VersionStamp WHERE DocumentVersionId = @v AND TamperedAt IS NOT NULL", ("@v", versionId)));
        Assert.Equal(1, await db.ScalarAsync<int>("SELECT COUNT(*) FROM audit.vSignedVersionTampering WHERE DocumentVersionId = @v", ("@v", versionId)));
    }

    [Fact]
    public async Task Api_rebuild_of_derived_columns_only_is_not_audited_and_does_not_mark_tampering()
    {
        await using var db = await database.OpenRolledBackTransactionAsync(Ct);
        var (versionId, nodeId) = await ContentNodeAsync(db, VersionState.Signed);
        var stampBefore = await StampAsync(db, versionId);

        await AsApiAsync(db, userId: 0);
        await db.ExecuteAsync(
            "UPDATE app.NodeContent SET ContentHtml = N'<p>x</p>', PlainText = N'x', ContentHash = CAST(REPLICATE(0x01, 32) AS varbinary(32)), DerivedStale = 0 WHERE NodeId = @n;",
            ("@n", nodeId));
        await db.ExecuteAsync("REVERT;");

        Assert.Empty(await ChangesAsync(db, "app.NodeContent", nodeId, "U"));
        Assert.Equal(stampBefore, await StampAsync(db, versionId));
        Assert.False(await IsTamperedAsync(db, versionId));
        Assert.False(await db.ScalarAsync<bool>("SELECT DerivedStale FROM app.NodeContent WHERE NodeId = @n", ("@n", nodeId)));
    }

    [Fact]
    public async Task Script_forging_derived_content_of_a_signed_version_is_audited_marks_tampering_and_is_re_rendered()
    {
        await using var db = await database.OpenRolledBackTransactionAsync(Ct);
        var (versionId, nodeId) = await ContentNodeAsync(db, VersionState.Signed);
        var support = await db.Data.DatabaseUserInRoleAsync("support_writer");

        await AsUserAsync(db, support);
        await db.ExecuteAsync("UPDATE app.NodeContent SET ContentHtml = N'<p>FORGED</p>', PlainText = N'FORGED', DerivedStale = 0 WHERE NodeId = @n;", ("@n", nodeId));
        await db.ExecuteAsync("REVERT;");

        var row = Assert.Single(await ChangesAsync(db, "app.NodeContent", nodeId, "U"));
        Assert.Equal("Script", row["Source"]);
        Assert.Equal("ContentHtml,PlainText", row["ChangedColumns"]);
        Assert.True(await db.ScalarAsync<bool>("SELECT DerivedStale FROM app.NodeContent WHERE NodeId = @n", ("@n", nodeId)));
        Assert.True(await IsTamperedAsync(db, versionId));
    }

    [Theory]
    [InlineData("support_writer", "UPDATE app.VersionStamp SET TamperedAt = NULL, LastChangeLogId = 1;")]
    [InlineData("support_writer", "DELETE FROM app.VersionStamp;")]
    [InlineData("support_writer", "INSERT INTO app.VersionStamp (DocumentVersionId, LastChangeLogId) SELECT TOP (1) Id, 1 FROM app.DocumentVersion;")]
    [InlineData("app_api", "UPDATE app.VersionStamp SET TamperedAt = NULL;")]
    [InlineData("support_writer", "DELETE FROM app.ContentStyleUsage;")]
    [InlineData("support_writer", "INSERT INTO app.ContentStyleUsage (StyleId, NodeId) SELECT TOP (1) 'Quote', NodeId FROM app.NodeContent;")]
    public async Task Version_stamp_and_style_usage_cannot_be_changed_directly(string role, string sql)
    {
        await using var db = await database.OpenRolledBackTransactionAsync(Ct);
        var (versionId, _) = await ContentNodeAsync(db, VersionState.Signed);
        await db.ExecuteAsync("UPDATE app.DocumentNode SET Title = N'Tampered' WHERE DocumentVersionId = @v;", ("@v", versionId));
        var user = await db.Data.DatabaseUserInRoleAsync(role);
        await AsUserAsync(db, user);

        var exception = await Assert.ThrowsAsync<SqlException>(() => db.ExecuteAsync(sql));

        Assert.Equal(PermissionDenied, exception.Number);
    }

    [Fact]
    public async Task Moving_a_node_out_of_a_signed_version_marks_the_signed_version_as_tampered()
    {
        await using var db = await database.OpenRolledBackTransactionAsync(Ct);
        var documentId = await db.Data.DocumentAsync();
        var signedId = await db.Data.VersionAsync(documentId);
        var nodeId = await db.Data.NodeAsync(signedId);
        await SignAsApiAsync(db, signedId);
        var draftId = await db.Data.VersionAsync(documentId);
        var signedStampBefore = await StampAsync(db, signedId);

        await db.ExecuteAsync("UPDATE app.DocumentNode SET DocumentVersionId = @d WHERE Id = @n;", ("@d", draftId), ("@n", nodeId));

        Assert.True(await StampAsync(db, signedId) > signedStampBefore);
        Assert.True(await IsTamperedAsync(db, signedId));
        Assert.False(await IsTamperedAsync(db, draftId));
        Assert.Equal(1, await db.ScalarAsync<int>(
            "SELECT COUNT(*) FROM audit.vSignedVersionTampering WHERE DocumentVersionId = @v AND TableName = N'app.DocumentNode' AND Operation = 'U'", ("@v", signedId)));
    }

    [Fact]
    public async Task Unsigning_and_re_signing_by_script_marks_the_version_as_tampered()
    {
        await using var db = await database.OpenRolledBackTransactionAsync(Ct);
        var (versionId, nodeId) = await ContentNodeAsync(db, VersionState.Signed);
        var support = await db.Data.DatabaseUserInRoleAsync("support_writer");

        await AsUserAsync(db, support);
        await db.ExecuteAsync("UPDATE app.DocumentVersion SET Status = 1, VersionNumber = NULL, SignedAt = NULL WHERE Id = @v;", ("@v", versionId));
        await db.ExecuteAsync("UPDATE app.NodeContent SET ContentJson = @j WHERE NodeId = @n;", ("@j", NewJson), ("@n", nodeId));
        await db.ExecuteAsync(
            "UPDATE app.DocumentVersion SET Status = 2, VersionNumber = 1, SignedAt = SYSUTCDATETIME(), SignedContentHash = CAST(REPLICATE(0x05, 32) AS varbinary(32)) WHERE Id = @v;",
            ("@v", versionId));
        await db.ExecuteAsync("REVERT;");

        Assert.True(await IsTamperedAsync(db, versionId));
        Assert.Equal(3, await db.ScalarAsync<int>("SELECT COUNT(*) FROM audit.vSignedVersionTampering WHERE DocumentVersionId = @v", ("@v", versionId)));
    }

    [Fact]
    public async Task Script_signing_a_draft_or_inserting_a_signed_version_marks_tampering()
    {
        await using var db = await database.OpenRolledBackTransactionAsync(Ct);
        var documentId = await db.Data.DocumentAsync();
        var draftId = await db.Data.VersionAsync(documentId);
        var support = await db.Data.DatabaseUserInRoleAsync("support_writer");

        await AsUserAsync(db, support);
        await db.ExecuteAsync("UPDATE app.DocumentVersion SET Status = 2, VersionNumber = 1, SignedAt = SYSUTCDATETIME() WHERE Id = @v;", ("@v", draftId));
        await db.ExecuteAsync("REVERT;");
        var otherDocumentId = await db.Data.DocumentAsync();
        await AsUserAsync(db, support);
        await db.ExecuteAsync(
            "INSERT INTO app.DocumentVersion (DocumentId, Status, VersionNumber, SignedAt, CreatedByUserId) VALUES (@d, 2, 1, SYSUTCDATETIME(), 2);",
            ("@d", otherDocumentId));
        await db.ExecuteAsync("REVERT;");
        var insertedSignedId = await db.ScalarAsync<int>("SELECT Id FROM app.DocumentVersion WHERE DocumentId = @d", ("@d", otherDocumentId));

        Assert.True(await IsTamperedAsync(db, draftId));
        Assert.True(await IsTamperedAsync(db, insertedSignedId));
    }

    [Fact]
    public async Task Api_signing_collected_signatures_does_not_mark_tampering()
    {
        await using var db = await database.OpenRolledBackTransactionAsync(Ct);
        var versionId = await db.Data.VersionAsync(await db.Data.DocumentAsync());

        await AsApiAsync(db, userId: CarolUserId);
        await db.ExecuteAsync("INSERT INTO app.VersionSignature (DocumentVersionId, UserId, ContentHash) VALUES (@v, 4, CAST(REPLICATE(0x03, 32) AS varbinary(32)));", ("@v", versionId));
        await db.ExecuteAsync("UPDATE app.DocumentVersion SET Status = 2, VersionNumber = 1, SignedAt = SYSUTCDATETIME() WHERE Id = @v;", ("@v", versionId));
        await db.ExecuteAsync("REVERT;");

        Assert.False(await IsTamperedAsync(db, versionId));
    }

    [Theory]
    [InlineData("INSERT INTO app.VersionSignature (DocumentVersionId, UserId, ContentHash) VALUES (@v, 5, CAST(REPLICATE(0x07, 32) AS varbinary(32)));")]
    [InlineData("UPDATE app.VersionSignature SET WithdrawnAt = SYSUTCDATETIME() WHERE DocumentVersionId = @v;")]
    [InlineData("UPDATE app.VersionSignature SET ContentHash = CAST(REPLICATE(0x08, 32) AS varbinary(32)) WHERE DocumentVersionId = @v;")]
    [InlineData("DELETE FROM app.VersionSignature WHERE DocumentVersionId = @v;")]
    public async Task Script_change_to_signatures_of_a_signed_version_marks_tampering(string sql)
    {
        await using var db = await database.OpenRolledBackTransactionAsync(Ct);
        var versionId = await db.Data.VersionAsync(await db.Data.DocumentAsync());
        await AsApiAsync(db, userId: CarolUserId);
        await db.ExecuteAsync("INSERT INTO app.VersionSignature (DocumentVersionId, UserId, ContentHash) VALUES (@v, 4, CAST(REPLICATE(0x03, 32) AS varbinary(32)));", ("@v", versionId));
        await db.ExecuteAsync("UPDATE app.DocumentVersion SET Status = 2, VersionNumber = 1, SignedAt = SYSUTCDATETIME() WHERE Id = @v;", ("@v", versionId));
        await db.ExecuteAsync("REVERT;");
        var support = await db.Data.DatabaseUserInRoleAsync("support_writer");

        await AsUserAsync(db, support);
        await db.ExecuteAsync(sql, ("@v", versionId));
        await db.ExecuteAsync("REVERT;");

        Assert.True(await IsTamperedAsync(db, versionId));
    }

    [Fact]
    public async Task Script_insert_of_content_with_derived_columns_is_flagged_stale()
    {
        await using var db = await database.OpenRolledBackTransactionAsync(Ct);
        var nodeId = await db.Data.NodeAsync(await db.Data.VersionAsync(await db.Data.DocumentAsync()));
        var support = await db.Data.DatabaseUserInRoleAsync("support_writer");

        await AsUserAsync(db, support);
        await db.ExecuteAsync(
            """
            INSERT INTO app.NodeContent (NodeId, DocumentVersionId, LogicalNodeId, ContentJson, ContentHtml, PlainText, ContentHash, ModifiedByUserId)
            SELECT n.Id, n.DocumentVersionId, n.LogicalNodeId, @j, N'<p>forged</p>', N'forged', CAST(REPLICATE(0x09, 32) AS varbinary(32)), 2
            FROM app.DocumentNode AS n WHERE n.Id = @n;
            """, ("@j", NewJson), ("@n", nodeId));
        await db.ExecuteAsync("REVERT;");

        Assert.True(await db.ScalarAsync<bool>("SELECT DerivedStale FROM app.NodeContent WHERE NodeId = @n", ("@n", nodeId)));
    }

    [Fact]
    public async Task Api_insert_of_content_is_not_flagged_stale()
    {
        await using var db = await database.OpenRolledBackTransactionAsync(Ct);
        var nodeId = await db.Data.NodeAsync(await db.Data.VersionAsync(await db.Data.DocumentAsync()));

        await AsApiAsync(db, userId: AliceUserId);
        await db.Data.ContentAsync(nodeId);
        await db.ExecuteAsync("REVERT;");

        Assert.False(await db.ScalarAsync<bool>("SELECT DerivedStale FROM app.NodeContent WHERE NodeId = @n", ("@n", nodeId)));
    }

    [Fact]
    public async Task Making_a_new_draft_current_does_not_mark_the_signed_version_as_tampered()
    {
        await using var db = await database.OpenRolledBackTransactionAsync(Ct);
        var (versionId, _) = await ContentNodeAsync(db, VersionState.Signed);
        await db.ExecuteAsync("UPDATE app.DocumentVersion SET IsCurrent = 1 WHERE Id = @v;", ("@v", versionId));

        await db.ExecuteAsync("UPDATE app.DocumentVersion SET IsCurrent = 0 WHERE Id = @v;", ("@v", versionId));

        Assert.False(await IsTamperedAsync(db, versionId));
    }

    [Fact]
    public async Task Api_save_rewriting_derived_columns_is_audited_but_not_flagged_stale()
    {
        await using var db = await database.OpenRolledBackTransactionAsync(Ct);
        var (_, nodeId) = await ContentNodeAsync(db);

        await AsApiAsync(db, userId: AliceUserId);
        await db.ExecuteAsync(
            """
            UPDATE app.NodeContent
            SET ContentJson = @j, ContentHtml = N'<p>Changed</p>', PlainText = N'Changed', ContentHash = CAST(REPLICATE(0x02, 32) AS varbinary(32)), DerivedStale = 0
            WHERE NodeId = @n;
            """, ("@j", NewJson), ("@n", nodeId));

        await db.ExecuteAsync("REVERT;");

        var row = Assert.Single(await ChangesAsync(db, "app.NodeContent", nodeId, "U"));
        Assert.Equal("App", row["Source"]);
        Assert.Equal("ContentJson,ContentHtml,PlainText,ContentHash", row["ChangedColumns"]);
        Assert.False(await db.ScalarAsync<bool>("SELECT DerivedStale FROM app.NodeContent WHERE NodeId = @n", ("@n", nodeId)));
    }

    [Fact]
    public async Task Signing_and_signatures_advance_the_version_stamp()
    {
        await using var db = await database.OpenRolledBackTransactionAsync(Ct);
        var (versionId, _) = await ContentNodeAsync(db);
        var afterCreate = await StampAsync(db, versionId);

        await AsApiAsync(db, userId: CarolUserId);
        await db.ExecuteAsync("INSERT INTO app.VersionSignature (DocumentVersionId, UserId, ContentHash) VALUES (@v, 4, CAST(REPLICATE(0x03, 32) AS varbinary(32)));", ("@v", versionId));
        var afterSignature = await StampAsync(db, versionId);
        await db.ExecuteAsync("UPDATE app.DocumentVersion SET Status = 2, VersionNumber = 1, SignedAt = SYSUTCDATETIME() WHERE Id = @v;", ("@v", versionId));
        var afterSigning = await StampAsync(db, versionId);
        await db.ExecuteAsync("REVERT;");

        Assert.True(afterSignature > afterCreate);
        Assert.True(afterSigning > afterSignature);
        Assert.Equal(0, await db.ScalarAsync<int>("SELECT COUNT(*) FROM app.VersionStamp WHERE DocumentVersionId = @v AND TamperedAt IS NOT NULL", ("@v", versionId)));
    }

    [Fact]
    public async Task Node_changes_advance_the_stamp_and_tampering_is_recorded_once()
    {
        await using var db = await database.OpenRolledBackTransactionAsync(Ct);
        var (versionId, nodeId) = await ContentNodeAsync(db, VersionState.Signed);
        var before = await StampAsync(db, versionId);

        await db.ExecuteAsync("UPDATE app.DocumentNode SET Title = N'First edit' WHERE Id = @n;", ("@n", nodeId));
        var first = await db.QueryAsync("SELECT LastChangeLogId, TamperedAt FROM app.VersionStamp WHERE DocumentVersionId = @v", ("@v", versionId));
        await db.ExecuteAsync("WAITFOR DELAY '00:00:00.020'; UPDATE app.DocumentNode SET Title = N'Second edit' WHERE Id = @n;", ("@n", nodeId));
        var second = await db.QueryAsync("SELECT LastChangeLogId, TamperedAt FROM app.VersionStamp WHERE DocumentVersionId = @v", ("@v", versionId));

        Assert.True((long)first[0]["LastChangeLogId"]! > before);
        Assert.True((long)second[0]["LastChangeLogId"]! > (long)first[0]["LastChangeLogId"]!);
        Assert.NotNull(first[0]["TamperedAt"]);
        Assert.Equal(first[0]["TamperedAt"], second[0]["TamperedAt"]);
    }

    [Fact]
    public async Task Change_log_is_partitioned_by_month_and_page_compressed()
    {
        await using var db = await database.OpenRolledBackTransactionAsync(Ct);

        var rows = await db.QueryAsync(
            """
            SELECT COUNT(*) AS Partitions, MIN(p.data_compression_desc) AS MinCompression, MAX(p.data_compression_desc) AS MaxCompression
            FROM sys.partitions AS p
            WHERE p.object_id = OBJECT_ID(N'audit.ChangeLog') AND p.index_id = 1;
            """);

        Assert.True((int)rows[0]["Partitions"]! >= 121);
        Assert.Equal("PAGE", rows[0]["MinCompression"]);
        Assert.Equal("PAGE", rows[0]["MaxCompression"]);
    }

    private enum VersionState
    {
        Draft,
        Signed,
    }

    private static async Task<(int VersionId, int NodeId)> ContentNodeAsync(RolledBackScope db, VersionState state = VersionState.Draft)
    {
        // Content is created in a draft; "Signed" then signs the draft, exactly like the lifecycle does.
        var versionId = await db.Data.VersionAsync(await db.Data.DocumentAsync());
        var nodeId = await db.Data.NodeAsync(versionId);
        await db.Data.ContentAsync(nodeId);
        if (state == VersionState.Signed)
        {
            await SignAsApiAsync(db, versionId);
        }

        return (versionId, nodeId);
    }

    /// <summary>Signs a draft the way the API does (as app_api), so the signing itself is not tampering.</summary>
    private static async Task SignAsApiAsync(RolledBackScope db, int versionId)
    {
        await AsApiAsync(db, userId: CarolUserId);
        await db.ExecuteAsync(
            "WAITFOR DELAY '00:00:00.010'; UPDATE app.DocumentVersion SET Status = 2, VersionNumber = 1, SignedAt = SYSUTCDATETIME() WHERE Id = @v; WAITFOR DELAY '00:00:00.010';",
            ("@v", versionId));
        await db.ExecuteAsync("REVERT;");
    }

    private static async Task AsApiAsync(RolledBackScope db, int userId)
    {
        var api = await db.Data.DatabaseUserInRoleAsync("app_api");
        await AsUserAsync(db, api, $"EXEC sp_set_session_context N'UserId', {userId.ToString(System.Globalization.CultureInfo.InvariantCulture)};");
    }

    private static async Task<bool> IsTamperedAsync(RolledBackScope db, int versionId) =>
        await db.ScalarAsync<int>("SELECT COUNT(*) FROM app.VersionStamp WHERE DocumentVersionId = @v AND TamperedAt IS NOT NULL", ("@v", versionId)) == 1;

    private static async Task AsUserAsync(RolledBackScope db, string user, string? thenSql = null) =>
        await db.ExecuteAsync($"EXECUTE AS USER = N'{user}'; {thenSql}");

    private static Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ChangesAsync(RolledBackScope db, string table, int entityId, string operation) =>
        db.QueryAsync(
            "SELECT * FROM audit.ChangeLog WHERE TableName = @t AND EntityId = @e AND Operation = @o ORDER BY Id;",
            ("@t", table), ("@e", entityId), ("@o", operation));

    private static Task<int> CountAsync(RolledBackScope db, string table, int versionId, string operation) =>
        db.ScalarAsync<int>(
            "SELECT COUNT(*) FROM audit.ChangeLog WHERE TableName = @t AND DocumentVersionId = @v AND Operation = @o;",
            ("@t", table), ("@v", versionId), ("@o", operation));

    private static Task<long> StampAsync(RolledBackScope db, int versionId) =>
        db.ScalarAsync<long>("SELECT LastChangeLogId FROM app.VersionStamp WHERE DocumentVersionId = @v", ("@v", versionId));

    private static IReadOnlyDictionary<string, object?> Row(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, string table) =>
        Assert.Single(rows, r => (string)r["TableName"]! == table);

    private static string SupportTemplate(int nodeId) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Support", "_TEMPLATE.sql"))
            .Replace("$(Ticket)", "INC-7", StringComparison.Ordinal)
            .Replace("$(Reason)", "Template test", StringComparison.Ordinal)
            .Replace("$(ContentJson)", NewJson, StringComparison.Ordinal)
            .Replace("$(NodeId)", nodeId.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
}
