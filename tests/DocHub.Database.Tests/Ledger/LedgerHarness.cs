using DocHub.Testing.Database;

namespace DocHub.Database.Tests.Ledger;

/// <summary>
/// Committed-data helpers for the T21 ledger tests. Reconciliation evaluates committed ledger transactions and their
/// principals, so these tests use real logins and no test transaction; each test reconciles only its own time window.
/// </summary>
internal sealed class LedgerHarness(DocHubDatabaseFixture database)
{
    public const string DerivedHtml = "<p>x</p>";

    /// <summary>The DBA / pipeline: the container's <c>sa</c> login (dbo).</summary>
    public Task<SqlSession> DboAsync() => SqlSession.OpenAsync(database.ConnectionString);

    /// <summary>An <c>app_api</c> login with the API's session context (UserId set), as the API writes.</summary>
    public async Task<SqlSession> ApiAsync(string? userName = null, int userId = TestData.AliceUserId)
    {
        var session = await SqlSession.OpenAsync(await SqlLogins.CreateAsync(database, userName, "app_api"));
        await session.ExecuteAsync("EXEC sys.sp_set_session_context @key = N'UserId', @value = @u;", ("@u", userId));
        return session;
    }

    /// <summary>A login in the given roles, without session context.</summary>
    public async Task<SqlSession> LoginAsync(params string[] roles) =>
        await SqlSession.OpenAsync(await SqlLogins.CreateAsync(database, null, roles));

    public static Task<DateTime> NowAsync(ISqlCommands db) => db.ScalarAsync<DateTime>("SELECT SYSUTCDATETIME();");

    /// <summary>Runs reconciliation as an <c>app_api</c> login (as the API does) and returns the number of new findings.</summary>
    public async Task<int> ReconcileAsync(DateTime from, string? digest = null)
    {
        await using var api = await ApiAsync(userId: 0);
        return await ReconcileAsync(api, from, digest);
    }

    public static async Task<int> ReconcileAsync(SqlSession session, DateTime from, string? digest = null)
    {
        await using var command = new Microsoft.Data.SqlClient.SqlCommand("audit.usp_ReconcileLedger", session.Connection)
        {
            CommandType = System.Data.CommandType.StoredProcedure,
            CommandTimeout = 120,
        };
        command.Parameters.AddWithValue("@From", from);
        command.Parameters.AddWithValue("@Digest", (object?)digest ?? DBNull.Value);
        var newFindings = command.Parameters.Add("@NewFindings", System.Data.SqlDbType.Int);
        newFindings.Direction = System.Data.ParameterDirection.Output;
        await command.ExecuteNonQueryAsync();
        return (int)newFindings.Value;
    }

    public static Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> FindingsAsync(ISqlCommands db, DateTime since) =>
        db.QueryAsync("SELECT * FROM audit.ReconciliationFinding WHERE DetectedAt >= @since ORDER BY Id;", ("@since", since));

    public static async Task<bool> ModifiedAfterSigningAsync(ISqlCommands db, int versionId) =>
        await db.ScalarAsync<int>("SELECT COUNT(*) FROM audit.vModifiedAfterSigning WHERE DocumentVersionId = @v;", ("@v", versionId)) == 1;

    /// <summary>The ledger transaction of the latest change of a row of an audited table.</summary>
    public static Task<long> LastTransactionAsync(ISqlCommands db, string table, string keyColumn, int id) =>
        db.ScalarAsync<long>($"SELECT MAX(ledger_transaction_id) FROM app.{table}_Ledger WHERE [{keyColumn}] = @id;", ("@id", id));

    /// <summary>A document with one version, one node and its content, written through the API login.</summary>
    public static async Task<(int DocumentId, int VersionId, int NodeId)> DocumentAsync(SqlSession api, bool signed)
    {
        var documentId = await api.Data.DocumentAsync();
        var versionId = await api.Data.VersionAsync(documentId, isCurrent: true);
        var nodeId = await api.Data.NodeAsync(versionId);
        await api.Data.ContentAsync(nodeId);
        if (signed)
        {
            await SignAsync(api, versionId);
        }

        return (documentId, versionId, nodeId);
    }

    /// <summary>Signs a draft as the API does (T07): signature first, then the version.</summary>
    public static async Task SignAsync(SqlSession api, int versionId)
    {
        await api.ExecuteAsync(
            """
            INSERT INTO app.VersionSignature (DocumentVersionId, UserId, ContentHash) VALUES (@v, @u, HASHBYTES('SHA2_256', N'signed'));
            UPDATE app.DocumentVersion SET Status = 2, VersionNumber = 1, SignedAt = SYSUTCDATETIME() WHERE Id = @v;
            """,
            ("@v", versionId), ("@u", TestData.BobUserId));
    }

    /// <summary>Changes content with the audit trigger disabled (a privileged bypass), in one transaction.</summary>
    public static Task BypassContentEditAsync(SqlSession dbo, int nodeId, string extraSql = "") =>
        dbo.ExecuteAsync(
            $"""
            SET XACT_ABORT ON;
            BEGIN TRANSACTION;
            DISABLE TRIGGER app.TR_NodeContent_Audit ON app.NodeContent;
            DISABLE TRIGGER app.TR_DocumentVersion_Audit ON app.DocumentVersion;
            UPDATE app.NodeContent SET ContentJson = JSON_MODIFY(ContentJson, '$.forged', CONVERT(NVARCHAR (36), NEWID())) WHERE NodeId = @n;
            {extraSql}
            ENABLE TRIGGER app.TR_DocumentVersion_Audit ON app.DocumentVersion;
            ENABLE TRIGGER app.TR_NodeContent_Audit ON app.NodeContent;
            COMMIT TRANSACTION;
            """,
            ("@n", nodeId));

    public static async Task<string> DigestAsync(ISqlCommands db)
    {
        var rows = await db.QueryAsync("EXEC sys.sp_generate_database_ledger_digest;");
        return (string)rows[0]["latest_digest"]!;
    }
}
