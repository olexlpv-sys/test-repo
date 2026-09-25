using DocHub.Testing.Database;
using Microsoft.Data.SqlClient;

namespace DocHub.Database.Tests.Ledger;

/// <summary>A fresh publish: ledger verification and reconciliation over all time are clean (T21 §2, §4 rule 4).</summary>
public sealed class FreshLedgerTests(DocHubDatabaseFixture database) : IClassFixture<DocHubDatabaseFixture>
{
    private readonly LedgerHarness _ledger = new(database);

    [Fact]
    public async Task Fresh_publish_verifies_and_reconciles_without_findings_as_the_api_login()
    {
        await using var api = await _ledger.ApiAsync(userId: 0);

        // ledger_reader (app_api is a member) allows digest generation and verification; snapshot isolation is on (DACPAC).
        var digest = await LedgerHarness.DigestAsync(api);
        var verification = await api.QueryAsync("EXEC sys.sp_verify_database_ledger @digest;", ("@digest", digest));
        Assert.Single(verification);

        Assert.Equal(0, await LedgerHarness.ReconcileAsync(api, new DateTime(2000, 1, 1), digest));
        Assert.Equal(0, await api.ScalarAsync<int>("SELECT COUNT(*) FROM audit.ReconciliationFinding;"));
    }
}

/// <summary>Temporal + ledger guarantees that hold for dbo too (T21 §1, §2).</summary>
public sealed class LedgerImmutabilityTests(DocHubDatabaseFixture database) : IClassFixture<DocHubDatabaseFixture>
{
    private readonly LedgerHarness _ledger = new(database);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_digest_that_does_not_match_the_ledger_is_a_ledger_integrity_finding_recorded_once()
    {
        await using var api = await _ledger.ApiAsync(userId: 0);
        var digest = await LedgerHarness.DigestAsync(api);
        var forged = System.Text.RegularExpressions.Regex.Replace(digest, "\"hash\":\"0x[0-9A-F]+\"", "\"hash\":\"0x" + new string('0', 64) + "\"");

        Assert.Equal(1, await LedgerHarness.ReconcileAsync(api, DateTime.UtcNow, forged));
        Assert.Equal(0, await LedgerHarness.ReconcileAsync(api, DateTime.UtcNow, forged));

        var finding = Assert.Single(await api.QueryAsync("SELECT * FROM audit.ReconciliationFinding WHERE Kind = 'LedgerIntegrity';"));
        Assert.Contains("Ledger verification failed", (string)finding["Detail"]!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task System_time_as_of_returns_the_earlier_state_of_content()
    {
        await using var db = await SqlSession.OpenAsync(database.ConnectionString);
        var documentId = await db.Data.DocumentAsync();
        var nodeId = await db.Data.NodeAsync(await db.Data.VersionAsync(documentId));
        await db.Data.ContentAsync(nodeId, """{"type":"doc","content":[],"v":1}""");
        var before = await db.ScalarAsync<DateTime>("SELECT SYSUTCDATETIME();");
        await db.ExecuteAsync("""UPDATE app.NodeContent SET ContentJson = N'{"type":"doc","content":[],"v":2}' WHERE NodeId = @n;""", ("@n", nodeId));

        var earlier = await db.ScalarAsync<string>("SELECT ContentJson FROM app.NodeContent FOR SYSTEM_TIME AS OF @t WHERE NodeId = @n;", ("@t", before), ("@n", nodeId));

        Assert.Contains("\"v\":1", earlier, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ALTER TABLE app.NodeContent SET (SYSTEM_VERSIONING = OFF);")]
    [InlineData("UPDATE history.NodeContent SET ContentJson = N'{}';")]
    [InlineData("DELETE FROM history.NodeContent;")]
    [InlineData("UPDATE history.VersionStamp SET TamperedAt = NULL;")]
    [InlineData("TRUNCATE TABLE audit.ChangeLog;")]
    public async Task Dbo_cannot_switch_off_versioning_or_rewrite_history(string sql)
    {
        await using var db = await database.OpenRolledBackTransactionAsync(Ct);
        var documentId = await db.Data.DocumentAsync();
        var nodeId = await db.Data.NodeAsync(await db.Data.VersionAsync(documentId));
        await db.Data.ContentAsync(nodeId);
        await db.ExecuteAsync("UPDATE app.NodeContent SET ContentJson = N'{\"type\":\"doc\",\"content\":[],\"x\":1}' WHERE NodeId = @n;", ("@n", nodeId));

        await Assert.ThrowsAsync<SqlException>(() => db.ExecuteAsync(sql));
    }
}

/// <summary>§2 baseline: transactions before the first baseline row are ignored.</summary>
public sealed class LedgerBaselineTests(DocHubDatabaseFixture database) : IClassFixture<DocHubDatabaseFixture>
{
    private readonly LedgerHarness _ledger = new(database);

    [Fact]
    public async Task Bulk_changes_before_the_baseline_are_not_findings_but_later_bypasses_are()
    {
        await using var dbo = await _ledger.DboAsync();
        await using var api = await _ledger.ApiAsync();
        var (_, _, nodeId) = await LedgerHarness.DocumentAsync(api, signed: false);

        // A bulk load with triggers disabled (e.g. the T19 data generator), then the baseline.
        await LedgerHarness.BypassContentEditAsync(dbo, nodeId);
        await dbo.ExecuteAsync("INSERT INTO audit.ReconciliationBaseline (CreatedAt, Reason) VALUES (SYSUTCDATETIME(), N'after bulk load');");

        Assert.Equal(0, await _ledger.ReconcileAsync(new DateTime(2000, 1, 1)));

        await LedgerHarness.BypassContentEditAsync(dbo, nodeId);
        Assert.Equal(1, await _ledger.ReconcileAsync(new DateTime(2000, 1, 1)));
    }
}

/// <summary>§4 rule 5 — the baseline table can't be replaced to move the baseline (one database per scenario).</summary>
public abstract class BaselineReplacementTests(DocHubDatabaseFixture database) : IClassFixture<DocHubDatabaseFixture>
{
    private readonly LedgerHarness _ledger = new(database);

    protected async Task ReplacingTheBaselineTableIsFoundAndTheOriginalBaselineStillApplies(string removal, string expectedDetail)
    {
        await using var dbo = await _ledger.DboAsync();
        await using var api = await _ledger.ApiAsync();
        var (_, _, nodeId) = await LedgerHarness.DocumentAsync(api, signed: false);
        await dbo.ExecuteAsync("INSERT INTO audit.ReconciliationBaseline (CreatedAt, Reason) VALUES (SYSUTCDATETIME(), N'go-live');");
        await LedgerHarness.BypassContentEditAsync(dbo, nodeId);

        await dbo.ExecuteAsync(removal);
        await dbo.ExecuteAsync(LedgerTables.BaselineTable);
        await dbo.ExecuteAsync("INSERT INTO audit.ReconciliationBaseline (CreatedAt, Reason) VALUES ('2000-01-01', N'forged, later first row');");

        await _ledger.ReconcileAsync(new DateTime(2000, 1, 1));

        var findings = await dbo.QueryAsync("SELECT Kind, TableName, EntityId, Detail FROM audit.ReconciliationFinding;");
        Assert.Contains(findings, f => (string)f["Kind"]! == "SchemaTampering" && (string)f["TableName"]! == "audit.ReconciliationBaseline"
                                       && ((string)f["Detail"]!).Contains(expectedDetail, StringComparison.Ordinal));
        // The bypass after the original baseline is still found: the replacement's later first row doesn't move the baseline.
        Assert.Contains(findings, f => (string)f["Kind"]! == "TriggerBypass" && (int)f["EntityId"]! == nodeId);
    }
}

public sealed class BaselineDropTests(DocHubDatabaseFixture database) : BaselineReplacementTests(database)
{
    [Fact]
    public Task Dropping_and_recreating_the_baseline_table_is_found_and_the_original_baseline_still_applies() =>
        ReplacingTheBaselineTableIsFoundAndTheOriginalBaselineStillApplies("DROP TABLE audit.ReconciliationBaseline;", "Table DROP");
}

public sealed class BaselineRenameTests(DocHubDatabaseFixture database) : BaselineReplacementTests(database)
{
    [Fact]
    public Task Renaming_transferring_and_recreating_the_baseline_table_is_found_and_the_original_baseline_still_applies() =>
        ReplacingTheBaselineTableIsFoundAndTheOriginalBaselineStillApplies(
            "EXEC sp_rename N'audit.ReconciliationBaseline', N'OldBaseline'; EXEC (N'CREATE SCHEMA quarantine'); ALTER SCHEMA quarantine TRANSFER audit.OldBaseline;",
            "Table SCHEMA_TRANSFER");
}

/// <summary>§4 rule 5 — findings can't be made to vanish by replacing the findings table (one database per scenario).</summary>
public abstract class FindingTableReplacementTests(DocHubDatabaseFixture database) : IClassFixture<DocHubDatabaseFixture>
{
    private readonly LedgerHarness _ledger = new(database);

    protected async Task ReplacingTheFindingsTableIsFoundAndEarlierFindingsAreReimported(string removal, string expectedDetail)
    {
        await using var dbo = await _ledger.DboAsync();
        await using var api = await _ledger.ApiAsync();
        var from = await LedgerHarness.StartWindowAsync(dbo);
        var (_, _, nodeId) = await LedgerHarness.DocumentAsync(api, signed: true);
        await LedgerHarness.BypassContentEditAsync(dbo, nodeId);
        Assert.Equal(1, await _ledger.ReconcileAsync(from));
        var original = Assert.Single(await LedgerHarness.FindingsAsync(dbo, from));

        await dbo.ExecuteAsync(removal);
        await dbo.ExecuteAsync(LedgerTables.FindingTable);

        await _ledger.ReconcileAsync(from);

        var findings = await dbo.QueryAsync("SELECT * FROM audit.ReconciliationFinding;");
        Assert.Contains(findings, f => (string)f["Kind"]! == "SchemaTampering" && (string)f["TableName"]! == "audit.ReconciliationFinding"
                                       && ((string)f["Detail"]!).Contains(expectedDetail, StringComparison.Ordinal));
        var reimported = Assert.Single(findings, f => (string)f["Kind"]! == "TriggerBypass");
        Assert.Equal(original["EntityId"], reimported["EntityId"]);
        Assert.Equal(original["LedgerTransactionId"], reimported["LedgerTransactionId"]);
        Assert.Equal(original["AfterSigning"], reimported["AfterSigning"]);

        // Reconciliation's own ChangeLog rows of the original finding aren't forged rows, and a re-run adds nothing.
        Assert.DoesNotContain(findings, f => (string)f["Kind"]! == "ForgedAuditRow");
        Assert.Equal(0, await _ledger.ReconcileAsync(from));
    }
}

public sealed class FindingTableDropTests(DocHubDatabaseFixture database) : FindingTableReplacementTests(database)
{
    [Fact]
    public Task Dropping_and_recreating_the_findings_table_is_found_and_earlier_findings_are_reimported() =>
        ReplacingTheFindingsTableIsFoundAndEarlierFindingsAreReimported("DROP TABLE audit.ReconciliationFinding;", "Table DROP");
}

public sealed class FindingTableRenameTests(DocHubDatabaseFixture database) : FindingTableReplacementTests(database)
{
    [Fact]
    public Task Renaming_transferring_and_recreating_the_findings_table_is_found_and_earlier_findings_are_reimported() =>
        ReplacingTheFindingsTableIsFoundAndEarlierFindingsAreReimported(
            "EXEC sp_rename N'audit.ReconciliationFinding', N'OldFinding'; EXEC (N'CREATE SCHEMA quarantine'); ALTER SCHEMA quarantine TRANSFER audit.OldFinding;",
            "Table SCHEMA_TRANSFER");
}

/// <summary>§4 rule 5 — columns outside the allow-list.</summary>
public sealed class LedgerColumnTamperingTests(DocHubDatabaseFixture database) : IClassFixture<DocHubDatabaseFixture>
{
    private readonly LedgerHarness _ledger = new(database);

    [Fact]
    public async Task Adding_a_column_outside_the_allow_list_and_dropping_a_column_are_found()
    {
        await using var dbo = await _ledger.DboAsync();
        await dbo.ExecuteAsync("ALTER TABLE app.Comment ADD Smuggled INT NULL;");
        await dbo.ExecuteAsync("ALTER TABLE app.Comment DROP COLUMN Smuggled;");
        await dbo.ExecuteAsync("ALTER TABLE app.NodeType ADD Legit INT NULL; ALTER TABLE app.NodeType DROP COLUMN Legit;");

        await _ledger.ReconcileAsync(DateTime.UtcNow);

        var details = (await dbo.QueryAsync("SELECT TableName, Detail FROM audit.ReconciliationFinding WHERE Kind = 'SchemaTampering';"))
            .Select(f => $"{f["TableName"]}: {f["Detail"]}")
            .ToList();
        Assert.Contains("app.Comment: Column ADD: Smuggled", details);
        Assert.Contains("app.Comment: Column DROP: Smuggled", details);
        Assert.Contains("app.NodeType: Column DROP: Legit", details);
    }
}

/// <summary>Scripts of the audit ledger tables, as a privileged principal would re-create them.</summary>
internal static class LedgerTables
{
    public const string BaselineTable = """
        CREATE TABLE audit.ReconciliationBaseline
        (
            Id INT IDENTITY (1, 1) NOT NULL CONSTRAINT PK_ReconciliationBaseline_Replaced PRIMARY KEY,
            CreatedAt DATETIME2 (7) NOT NULL,
            Reason NVARCHAR (500) NOT NULL
        )
        WITH (LEDGER = ON (APPEND_ONLY = ON, LEDGER_VIEW = audit.ReconciliationBaseline_Ledger_Replaced));
        """;

    public const string FindingTable = """
        CREATE TABLE audit.ReconciliationFinding
        (
            Id BIGINT IDENTITY (1, 1) NOT NULL CONSTRAINT PK_ReconciliationFinding_Replaced PRIMARY KEY,
            Kind VARCHAR (40) NOT NULL, TableName NVARCHAR (256) NULL, EntityId INT NULL, DocumentId INT NULL, DocumentVersionId INT NULL,
            LedgerTransactionId BIGINT NULL, TransactionCommitTime DATETIME2 (7) NULL, Principal NVARCHAR (256) NULL, Detail NVARCHAR (1000) NULL,
            DetectedAt DATETIME2 (7) NOT NULL DEFAULT (SYSUTCDATETIME()), AfterSigning BIT NOT NULL DEFAULT (0)
        )
        WITH (LEDGER = ON (APPEND_ONLY = ON, LEDGER_VIEW = audit.ReconciliationFinding_Ledger_Replaced));
        """;
}
