using DocHub.Testing.Database;

namespace DocHub.Database.Tests.Ledger;

/// <summary>
/// T21 §4 rules 1–3 and the effects of findings. One database for the class; every test reconciles only the window that
/// starts with the test (tests of a class run sequentially), so findings of other tests don't interfere.
/// </summary>
public sealed class ReconciliationTests(DocHubDatabaseFixture database) : IClassFixture<DocHubDatabaseFixture>
{
    private readonly LedgerHarness _ledger = new(database);

    [Fact]
    public async Task Trigger_bypass_on_a_signed_version_is_found_and_flags_the_version()
    {
        await using var dbo = await _ledger.DboAsync();
        await using var api = await _ledger.ApiAsync();
        var from = await LedgerHarness.NowAsync(dbo);
        var (_, versionId, nodeId) = await LedgerHarness.DocumentAsync(api, signed: true);
        var stampBefore = await dbo.ScalarAsync<long>("SELECT LastChangeLogId FROM app.VersionStamp WHERE DocumentVersionId = @v;", ("@v", versionId));

        await LedgerHarness.BypassContentEditAsync(dbo, nodeId);
        var transaction = await LedgerHarness.LastTransactionAsync(dbo, "NodeContent", "NodeId", nodeId);

        Assert.Equal(1, await _ledger.ReconcileAsync(from));

        var finding = Assert.Single(await LedgerHarness.FindingsAsync(dbo, from));
        Assert.Equal("TriggerBypass", finding["Kind"]);
        Assert.Equal("app.NodeContent", finding["TableName"]);
        Assert.Equal(nodeId, finding["EntityId"]);
        Assert.Equal(versionId, finding["DocumentVersionId"]);
        Assert.Equal(transaction, finding["LedgerTransactionId"]);
        Assert.Equal(true, finding["AfterSigning"]);
        Assert.Equal("sa", finding["Principal"]);

        var stamp = Assert.Single(await dbo.QueryAsync("SELECT LastChangeLogId, TamperedAt FROM app.VersionStamp WHERE DocumentVersionId = @v;", ("@v", versionId)));
        Assert.NotNull(stamp["TamperedAt"]);
        Assert.True((long)stamp["LastChangeLogId"]! > stampBefore);
        var log = Assert.Single(await dbo.QueryAsync("SELECT * FROM audit.ChangeLog WHERE Id = @id;", ("@id", stamp["LastChangeLogId"])));
        Assert.Equal("audit.ReconciliationFinding", log["TableName"]);
        Assert.Equal("Reconciliation", log["OperationContext"]);
        Assert.Equal((int)(long)finding["Id"]!, log["EntityId"]);
        Assert.True(await dbo.ScalarAsync<bool>("SELECT DerivedStale FROM app.NodeContent WHERE NodeId = @n;", ("@n", nodeId)));
        Assert.True(await LedgerHarness.ModifiedAfterSigningAsync(dbo, versionId));
    }

    [Fact]
    public async Task Forged_audit_row_is_found()
    {
        await using var dbo = await _ledger.DboAsync();
        await using var api = await _ledger.ApiAsync();
        var from = await LedgerHarness.NowAsync(dbo);
        var (documentId, versionId, nodeId) = await LedgerHarness.DocumentAsync(api, signed: false);

        var logId = await dbo.ScalarAsync<long>(
            """
            INSERT INTO audit.ChangeLog (TableName, Operation, EntityId, DocumentId, DocumentVersionId, Source, DbLogin, UserId)
            VALUES (N'app.NodeContent', 'U', @n, @d, @v, 'App', N'dochub_api', 2);
            SELECT CAST(SCOPE_IDENTITY() AS BIGINT);
            """,
            ("@n", nodeId), ("@d", documentId), ("@v", versionId));
        var transaction = await dbo.ScalarAsync<long>("SELECT ledger_start_transaction_id FROM audit.ChangeLog WHERE Id = @id;", ("@id", logId));

        Assert.Equal(1, await _ledger.ReconcileAsync(from));

        var finding = Assert.Single(await LedgerHarness.FindingsAsync(dbo, from));
        Assert.Equal("ForgedAuditRow", finding["Kind"]);
        Assert.Equal("app.NodeContent", finding["TableName"]);
        Assert.Equal(nodeId, finding["EntityId"]);
        Assert.Equal(transaction, finding["LedgerTransactionId"]);
        Assert.Contains($"ChangeLog {logId}", (string)finding["Detail"]!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Clearing_TamperedAt_is_stamp_tampering_and_the_version_stays_flagged()
    {
        await using var dbo = await _ledger.DboAsync();
        await using var api = await _ledger.ApiAsync();
        var from = await LedgerHarness.NowAsync(dbo);
        var (_, versionId, nodeId) = await LedgerHarness.DocumentAsync(api, signed: true);
        // An audited script change of the signed version: the trigger sets TamperedAt (no finding — it is audited).
        await dbo.ExecuteAsync("UPDATE app.NodeContent SET ContentJson = JSON_MODIFY(ContentJson, '$.edited', 1) WHERE NodeId = @n;", ("@n", nodeId));
        Assert.True(await LedgerHarness.ModifiedAfterSigningAsync(dbo, versionId));

        await dbo.ExecuteAsync("UPDATE app.VersionStamp SET TamperedAt = NULL WHERE DocumentVersionId = @v;", ("@v", versionId));
        var transaction = await dbo.ScalarAsync<long>("SELECT MAX(ledger_transaction_id) FROM app.VersionStamp_Ledger WHERE DocumentVersionId = @v;", ("@v", versionId));
        Assert.False(await LedgerHarness.ModifiedAfterSigningAsync(dbo, versionId));

        Assert.Equal(1, await _ledger.ReconcileAsync(from));

        var finding = Assert.Single(await LedgerHarness.FindingsAsync(dbo, from));
        Assert.Equal("StampTampering", finding["Kind"]);
        Assert.Equal(versionId, finding["EntityId"]);
        Assert.Equal(versionId, finding["DocumentVersionId"]);
        Assert.Equal(transaction, finding["LedgerTransactionId"]);
        Assert.Equal(true, finding["AfterSigning"]);
        Assert.True(await LedgerHarness.ModifiedAfterSigningAsync(dbo, versionId));

        // Finding-based: clearing the stamp again doesn't hide it.
        await dbo.ExecuteAsync("UPDATE app.VersionStamp SET TamperedAt = NULL WHERE DocumentVersionId = @v;", ("@v", versionId));
        Assert.True(await LedgerHarness.ModifiedAfterSigningAsync(dbo, versionId));
    }

    [Fact]
    public async Task Normal_api_and_support_writes_and_no_op_updates_produce_no_findings()
    {
        await using var dbo = await _ledger.DboAsync();
        await using var api = await _ledger.ApiAsync();
        var from = await LedgerHarness.NowAsync(dbo);

        // API: create, save content (ContentJson + all derived columns), derived-only refresher update, sign, comment, delete.
        var (documentId, versionId, nodeId) = await LedgerHarness.DocumentAsync(api, signed: false);
        await api.ExecuteAsync(
            """
            EXEC sys.sp_set_session_context @key = N'OperationContext', @value = N'ApiContentSave';
            UPDATE app.NodeContent SET ContentJson = JSON_MODIFY(ContentJson, '$.saved', 1), ContentHtml = N'<p>a</p>', PlainText = N'a',
                   ContentHash = HASHBYTES('SHA2_256', N'a'), DerivedStale = 0 WHERE NodeId = @n;
            EXEC sys.sp_set_session_context @key = N'OperationContext', @value = N'RebuildDerived';
            UPDATE app.NodeContent SET ContentHtml = N'<p>b</p>', PlainText = N'b', ContentHash = HASHBYTES('SHA2_256', N'b'), DerivedStale = 0 WHERE NodeId = @n;
            EXEC sys.sp_set_session_context @key = N'OperationContext', @value = NULL;
            INSERT INTO app.Comment (DocumentId, DocumentVersionId, AuthorUserId, Body) VALUES (@d, @v, 2, N'c');
            DELETE FROM app.Comment WHERE DocumentId = @d;
            """,
            ("@n", nodeId), ("@d", documentId), ("@v", versionId));
        await LedgerHarness.SignAsync(api, versionId);
        var (_, draftId, draftNodeId) = await LedgerHarness.DocumentAsync(api, signed: false);

        // Support scripts, with and without context (audited as Script, T03).
        await using (var support = await _ledger.LoginAsync("support_writer"))
        {
            await support.ExecuteAsync("UPDATE app.NodeContent SET ContentJson = JSON_MODIFY(ContentJson, '$.s1', 1) WHERE NodeId = @n;", ("@n", draftNodeId));
            await support.ExecuteAsync(
                """
                EXEC audit.usp_SetSupportContext @Ticket = N'INC-1', @Reason = N'fix', @ActingUserId = 2;
                UPDATE app.NodeContent SET ContentJson = JSON_MODIFY(ContentJson, '$.s2', 1) WHERE NodeId = @n;
                UPDATE app.DocumentNode SET Title = N'Fixed' WHERE Id = @n;
                """,
                ("@n", draftNodeId));
        }

        // No-op updates (no audit row, and none needed), by the API and by dbo.
        await api.ExecuteAsync("UPDATE app.DocumentNode SET Title = Title WHERE Id = @n;", ("@n", nodeId));
        await dbo.ExecuteAsync("UPDATE app.NodeType SET Name = Name; UPDATE app.DocumentVersion SET IsCurrent = IsCurrent WHERE Id = @v;", ("@v", draftId));

        Assert.Equal(0, await _ledger.ReconcileAsync(from));
        Assert.Empty(await LedgerHarness.FindingsAsync(dbo, from));
    }

    [Fact]
    public async Task Reconciliation_is_idempotent()
    {
        await using var dbo = await _ledger.DboAsync();
        await using var api = await _ledger.ApiAsync();
        var from = await LedgerHarness.NowAsync(dbo);
        var (_, versionId, nodeId) = await LedgerHarness.DocumentAsync(api, signed: true);
        await LedgerHarness.BypassContentEditAsync(dbo, nodeId);
        await dbo.ExecuteAsync("INSERT INTO audit.ChangeLog (TableName, Operation, EntityId, Source, DbLogin) VALUES (N'app.Folder', 'U', 1, 'App', N'x');");
        Assert.Equal(2, await _ledger.ReconcileAsync(from));

        const string State = """
            SELECT (SELECT COUNT(*) FROM app.VersionStamp_Ledger WHERE DocumentVersionId = @v) AS StampChanges,
                   (SELECT COUNT(*) FROM audit.ChangeLog) AS LogRows,
                   (SELECT COUNT(*) FROM audit.ReconciliationFinding) AS Findings;
            """;
        var before = Assert.Single(await dbo.QueryAsync(State, ("@v", versionId)));

        Assert.Equal(0, await _ledger.ReconcileAsync(from));

        var after = Assert.Single(await dbo.QueryAsync(State, ("@v", versionId)));
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task Api_login_whose_name_differs_from_its_user_is_not_a_finding_but_a_db_owner_posing_as_the_api_is()
    {
        await using var dbo = await _ledger.DboAsync();
        var from = await LedgerHarness.NowAsync(dbo);
        await using (var api = await _ledger.ApiAsync(userName: $"api_user_{Guid.NewGuid():N}"))
        {
            await LedgerHarness.DocumentAsync(api, signed: true);
        }

        Assert.Equal(0, await _ledger.ReconcileAsync(from));

        // db_owner counts as 'App' for the triggers (local development), but its transactions aren't the API's.
        await using var owner = await _ledger.LoginAsync("db_owner");
        await owner.ExecuteAsync("EXEC sys.sp_set_session_context @key = N'UserId', @value = 2;");
        var folderId = await owner.ScalarAsync<int>("INSERT INTO app.Folder (ParentFolderId, Name, SortOrder, CreatedByUserId) VALUES (1, CONVERT(NVARCHAR (36), NEWID()), 1, 2); SELECT CAST(SCOPE_IDENTITY() AS INT);");
        Assert.Equal("App", await dbo.ScalarAsync<string>("SELECT Source FROM audit.ChangeLog WHERE TableName = N'app.Folder' AND EntityId = @f;", ("@f", folderId)));

        Assert.Equal(1, await _ledger.ReconcileAsync(from));

        var finding = Assert.Single(await LedgerHarness.FindingsAsync(dbo, from));
        Assert.Equal("ForgedAuditRow", finding["Kind"]);
        Assert.Equal("app.Folder", finding["TableName"]);
        Assert.Equal(folderId, finding["EntityId"]);
        Assert.Contains("is not an app_api member", (string)finding["Detail"]!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Moving_SignedAt_after_a_bypass_edit_does_not_hide_it()
    {
        await using var dbo = await _ledger.DboAsync();
        await using var api = await _ledger.ApiAsync();
        var from = await LedgerHarness.NowAsync(dbo);
        var (_, versionId, nodeId) = await LedgerHarness.DocumentAsync(api, signed: true);

        await LedgerHarness.BypassContentEditAsync(dbo, nodeId, $"UPDATE app.DocumentVersion SET SignedAt = DATEADD(MINUTE, 10, SYSUTCDATETIME()) WHERE Id = {versionId};");

        Assert.Equal(2, await _ledger.ReconcileAsync(from));

        Assert.All(await LedgerHarness.FindingsAsync(dbo, from), f => Assert.Equal(true, f["AfterSigning"]));
        Assert.True(await LedgerHarness.ModifiedAfterSigningAsync(dbo, versionId));
    }

    [Fact]
    public async Task Signing_a_draft_directly_is_flagged()
    {
        await using var dbo = await _ledger.DboAsync();
        await using var api = await _ledger.ApiAsync();
        var from = await LedgerHarness.NowAsync(dbo);
        var (_, versionId, nodeId) = await LedgerHarness.DocumentAsync(api, signed: false);

        await LedgerHarness.BypassContentEditAsync(dbo, nodeId, $"UPDATE app.DocumentVersion SET Status = 2, VersionNumber = 1, SignedAt = SYSUTCDATETIME() WHERE Id = {versionId};");

        Assert.Equal(2, await _ledger.ReconcileAsync(from));

        Assert.Contains(await LedgerHarness.FindingsAsync(dbo, from), f => (string)f["TableName"]! == "app.DocumentVersion" && (bool)f["AfterSigning"]!);
        Assert.True(await LedgerHarness.ModifiedAfterSigningAsync(dbo, versionId));
    }

    [Fact]
    public async Task Findings_on_a_draft_do_not_flag_the_version_after_signing_but_later_tampering_does()
    {
        await using var dbo = await _ledger.DboAsync();
        await using var api = await _ledger.ApiAsync();
        var from = await LedgerHarness.NowAsync(dbo);
        var (_, versionId, nodeId) = await LedgerHarness.DocumentAsync(api, signed: false);
        await LedgerHarness.BypassContentEditAsync(dbo, nodeId);
        Assert.Equal(1, await _ledger.ReconcileAsync(from));

        await LedgerHarness.SignAsync(api, versionId);
        Assert.Equal(0, await _ledger.ReconcileAsync(from));
        Assert.False(await LedgerHarness.ModifiedAfterSigningAsync(dbo, versionId));

        await LedgerHarness.BypassContentEditAsync(dbo, nodeId);
        Assert.Equal(1, await _ledger.ReconcileAsync(from));
        Assert.True(await LedgerHarness.ModifiedAfterSigningAsync(dbo, versionId));
        Assert.Equal([false, true], (await LedgerHarness.FindingsAsync(dbo, from)).Select(f => (bool)f["AfterSigning"]!));
    }

    [Fact]
    public async Task Reconciliation_refuses_to_run_inside_a_transaction()
    {
        await using var api = await _ledger.ApiAsync(userId: 0);
        var error = await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => api.ExecuteAsync(
            "BEGIN TRANSACTION; EXEC audit.usp_ReconcileLedger @From = '2000-01-01';"));
        Assert.Equal(50101, error.Number);
    }
}
