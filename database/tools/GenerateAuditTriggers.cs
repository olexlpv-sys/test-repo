// Generates, for the DocHub database project: the audit triggers (T03), the ChangeLog partition function, the ledger
// reconciliation procedure with its ledger allow-list (T21 §4) and the module hashes the API checks (T21 §4, rule 6).
// Usage (from the repository root):  dotnet run database/tools/GenerateAuditTriggers.cs
// The generated files are committed; CI re-runs this generator and fails if the output differs.
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

var root = FindRepositoryRoot();
var project = Path.Combine(root, "database", "DocHub.Database");

const string SignedVersion = "[v].[Status] = 2";

var tables = new[]
{
    new AuditedTable("User", "Id", [S("Login"), S("DisplayName"), S("Email"), C("IsAdmin"), C("IsActive")]),
    new AuditedTable("Folder", "Id", [C("ParentFolderId"), S("Name"), C("SortOrder"), C("CreatedAt"), C("CreatedByUserId")]),
    new AuditedTable("NodeType", "Id", [S("Code"), S("Name"), S("Description"), C("SortOrder"), C("IsActive")]),
    new AuditedTable("ContentStyle", "Id", [S("StyleId"), S("Name"), C("Kind"), S("BasedOnStyleId"), S("PropertiesJson"), C("IsBuiltIn"), C("IsActive")]),
    new AuditedTable("Document", "Id", [C("FolderId"), S("Title"), C("OwnerUserId"), C("CreatedAt"), C("DeletedAt"), C("DeletedByUserId")])
    {
        DocumentId = "{r}.[Id]",
    },
    new AuditedTable("DocumentVersion", "Id", [C("DocumentId"), C("Status"), C("VersionNumber"), C("BasedOnVersionId"), C("CreatedAt"), C("CreatedByUserId"), C("SignedAt"), C("SignedContentHash"), C("IsCurrent")])
    {
        DocumentId = "{r}.[DocumentId]",
        DocumentVersionId = "{r}.[Id]",
        AdvancesVersionStamp = true,
        // Tampering: (a) the signing data of a Signed version changes (unsign, re-sign, new hash/number/time; IsCurrent flips
        // legitimately), or (b) a script — not the API — creates a Signed version or signs a draft.
        TamperCondition = "([d].[Status] = 2 AND ([i].[Id] IS NULL OR [i].[Status] IS DISTINCT FROM [d].[Status] OR [i].[VersionNumber] IS DISTINCT FROM [d].[VersionNumber] OR [i].[SignedAt] IS DISTINCT FROM [d].[SignedAt] OR [i].[SignedContentHash] IS DISTINCT FROM [d].[SignedContentHash] OR [i].[DocumentId] IS DISTINCT FROM [d].[DocumentId]))"
            + " OR ([i].[Status] = 2 AND ([d].[Id] IS NULL OR [d].[Status] <> 2) AND [ctx].[Source] = 'Script')",
    },
    new AuditedTable("VersionSignature", "Id", [C("DocumentVersionId"), C("UserId"), C("SignedAt"), C("ContentHash"), C("WithdrawnAt"), S("Comment")])
    {
        DocumentVersionId = "{r}.[DocumentVersionId]",
        AdvancesVersionStamp = true,
        // Signatures are only collected while the version is a Draft (the API inserts the last one before finalizing), so any
        // signature change on a Signed version is tampering.
        TamperCondition = SignedVersion,
    },
    new AuditedTable("DocumentNode", "Id", [C("DocumentVersionId"), C("LogicalNodeId"), C("ParentNodeId"), C("NodeTypeId"), S("Title"), C("SortOrder"), C("CreatedAt"), C("CreatedByUserId"), C("ModifiedAt"), C("ModifiedByUserId")])
    {
        DocumentVersionId = "{r}.[DocumentVersionId]",
        LogicalNodeId = "{r}.[LogicalNodeId]",
        AdvancesVersionStamp = true,
        ChangesContent = true,
        TamperCondition = SignedVersion,
    },
    // Derived columns (ContentHtml, PlainText, ContentHash) are re-rendered by the API. An update that changes only them is
    // audited when it comes from a script (Source = 'Script'), and not audited when the API/refresher does it (T03 §2c).
    // Their values are not copied into the JSON images (volume), except ContentHash.
    new AuditedTable("NodeContent", "NodeId", [C("DocumentVersionId"), C("LogicalNodeId"), C("SchemaVersion"), S("ContentJson"), C("ModifiedAt"), C("ModifiedByUserId")])
    {
        DerivedColumns = [S("ContentHtml"), S("PlainText"), C("ContentHash")],
        LoggedDerivedColumns = ["ContentHash"],
        DocumentVersionId = "{r}.[DocumentVersionId]",
        LogicalNodeId = "{r}.[LogicalNodeId]",
        AdvancesVersionStamp = true,
        ChangesContent = true,
        TamperCondition = SignedVersion,
        FlagsDerivedStale = true,
    },
    new AuditedTable("DocumentPermission", "Id", [C("DocumentId"), C("UserId"), C("Role"), C("LogicalNodeId"), C("GrantedAt"), C("GrantedByUserId")])
    {
        DocumentId = "{r}.[DocumentId]",
        LogicalNodeId = "{r}.[LogicalNodeId]",
    },
    new AuditedTable("Comment", "Id", [C("DocumentId"), C("DocumentVersionId"), C("LogicalNodeId"), C("ParentCommentId"), C("AuthorUserId"), S("Body"), C("CreatedAt"), C("EditedAt"), C("DeletedAt"), C("ResolvedAt"), C("ResolvedByUserId")])
    {
        DocumentId = "{r}.[DocumentId]",
        DocumentVersionId = "{r}.[DocumentVersionId]",
        LogicalNodeId = "{r}.[LogicalNodeId]",
    },
};

var triggerDirectory = Path.Combine(project, "app", "Triggers");
Directory.CreateDirectory(triggerDirectory);
foreach (var stale in Directory.GetFiles(triggerDirectory, "TR_*_Audit.sql"))
{
    File.Delete(stale);
}

foreach (var table in tables)
{
    Write(Path.Combine(triggerDirectory, $"TR_{table.Name}_Audit.sql"), GenerateTrigger(table));
}

Write(Path.Combine(project, "audit", "Storage", "ChangeLogPartitioning.sql"), GeneratePartitioning(new DateOnly(2026, 1, 1), months: 120));

var allowList = ReadLedgerAllowList(project);
Write(Path.Combine(project, "audit", "StoredProcedures", "usp_ReconcileLedger.sql"), GenerateReconciliation(tables, allowList));

// Rule 6: hashes of every audit module (triggers, audit functions, procedures and views), taken after all of them are written.
var modules = Directory.GetFiles(Path.Combine(project, "audit"), "*.sql", SearchOption.AllDirectories)
    .Where(f => f.Contains($"{Path.DirectorySeparatorChar}Functions{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
             || f.Contains($"{Path.DirectorySeparatorChar}StoredProcedures{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
             || f.Contains($"{Path.DirectorySeparatorChar}Views{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
    .Concat(Directory.GetFiles(triggerDirectory, "TR_*_Audit.sql"))
    .Select(f => (Name: ModuleName(File.ReadAllText(f)), Hash: ModuleHash(File.ReadAllText(f))))
    .OrderBy(m => m.Name, StringComparer.Ordinal)
    .ToList();
Write(Path.Combine(root, "src", "DocHub.Infrastructure", "Audit", "AuditModuleHashes.g.cs"), GenerateModuleHashes(modules));

Console.WriteLine($"Generated {tables.Length} audit triggers, the ChangeLog partition function, the reconciliation procedure "
    + $"({allowList.Count} allow-listed ledger columns) and {modules.Count} module hashes.");
return;

static AuditedColumn S(string name) => new(name, IsString: true);

static AuditedColumn C(string name) => new(name, IsString: false);

static string GenerateTrigger(AuditedTable t)
{
    var key = t.KeyColumn;
    var r = $"COALESCE([i].[{key}], [d].[{key}])";
    string Resolve(string? template) => template is null ? "NULL" : Coalesced(template);
    string Coalesced(string template) => $"COALESCE({template.Replace("{r}", "[i]", StringComparison.Ordinal)}, {template.Replace("{r}", "[d]", StringComparison.Ordinal)})";

    var versionExpression = t.DocumentVersionId is null ? "NULL" : Coalesced(t.DocumentVersionId);
    var documentExpression = t.DocumentId is not null
        ? Coalesced(t.DocumentId)
        : t.DocumentVersionId is not null
            ? $"(SELECT [v].[DocumentId] FROM [app].[DocumentVersion] AS [v] WHERE [v].[Id] = {versionExpression})"
            : "NULL";

    var logged = t.Columns.Concat(t.DerivedColumns.Where(c => t.LoggedDerivedColumns.Contains(c.Name))).ToList();
    var changedAny = string.Join("\n           OR ", t.Columns.Select(c => Distinct(c)));
    var derivedAny = string.Join(" OR ", t.DerivedColumns.Select(c => Distinct(c)));
    var changedList = string.Join(",\n                   ", t.Columns.Concat(t.DerivedColumns).Select(c => $"CASE WHEN {Distinct(c)} THEN N'{c.Name}' END"));

    var sb = new StringBuilder();
    sb.AppendLine("-- <auto-generated> by database/tools/GenerateAuditTriggers.cs — do not edit by hand. </auto-generated>");
    sb.AppendLine(CultureInfo.InvariantCulture, $"CREATE TRIGGER [app].[TR_{t.Name}_Audit]");
    sb.AppendLine(CultureInfo.InvariantCulture, $"ON [app].[{t.Name}]");
    sb.AppendLine("AFTER INSERT, UPDATE, DELETE");
    sb.AppendLine("AS");
    sb.AppendLine("BEGIN");
    sb.AppendLine("    SET NOCOUNT ON;");
    sb.AppendLine();
    sb.AppendLine("    IF NOT EXISTS (SELECT 1 FROM inserted) AND NOT EXISTS (SELECT 1 FROM deleted)");
    sb.AppendLine("        RETURN;");
    sb.AppendLine();

    if (t.FlagsDerivedStale)
    {
        sb.AppendLine("    -- T03 §2a: derived columns become stale when ContentJson changes without all derived columns rewritten in the same");
        sb.AppendLine("    -- statement (the API save rewrites them), and whenever a script touches ContentJson or any derived column — the API");
        sb.AppendLine("    -- then re-renders them from ContentJson, so forged HTML/plain text is never served. Column- and role-based only.");
        sb.AppendLine("    DECLARE @Source VARCHAR (10) = (SELECT [Source] FROM [audit].[fn_ChangeContext]());");
        sb.AppendLine("    DECLARE @DerivedRewritten BIT = CASE WHEN UPDATE([ContentHtml]) AND UPDATE([PlainText]) AND UPDATE([ContentHash]) THEN 1 ELSE 0 END;");
        sb.AppendLine();
        sb.AppendLine("    IF UPDATE([ContentJson]) OR UPDATE([ContentHtml]) OR UPDATE([PlainText]) OR UPDATE([ContentHash]) OR UPDATE([DerivedStale]) -- also true for INSERT");
        sb.AppendLine("    BEGIN");
        sb.AppendLine("        UPDATE [nc]");
        sb.AppendLine("        SET [DerivedStale] = 1");
        sb.AppendLine("        FROM [app].[NodeContent] AS [nc]");
        sb.AppendLine("        JOIN inserted AS [i] ON [i].[NodeId] = [nc].[NodeId]");
        sb.AppendLine("        LEFT JOIN deleted AS [d] ON [d].[NodeId] = [i].[NodeId]");
        sb.AppendLine("        WHERE [nc].[DerivedStale] = 0");
        sb.AppendLine("          AND (");
        sb.AppendLine("                ([d].[NodeId] IS NULL AND @Source = 'Script')");
        sb.AppendLine("             OR");
        sb.AppendLine("                (CAST([i].[ContentJson] AS VARBINARY (MAX)) IS DISTINCT FROM CAST([d].[ContentJson] AS VARBINARY (MAX))");
        sb.AppendLine("                 AND NOT (@DerivedRewritten = 1 AND [i].[ContentHash] IS DISTINCT FROM [d].[ContentHash]))");
        sb.AppendLine("             OR (@Source = 'Script' AND (CAST([i].[ContentJson] AS VARBINARY (MAX)) IS DISTINCT FROM CAST([d].[ContentJson] AS VARBINARY (MAX))");
        sb.AppendLine(CultureInfo.InvariantCulture, $"                                          OR {derivedAny}");
        sb.AppendLine("                                          OR [i].[DerivedStale] IS DISTINCT FROM [d].[DerivedStale])));");
        sb.AppendLine("    END;");
        sb.AppendLine();
    }

    var needsLogTable = t.AdvancesVersionStamp;
    if (needsLogTable)
    {
        sb.AppendLine("    DECLARE @Logged TABLE ([Id] BIGINT NOT NULL, [EntityId] INT NOT NULL, [ChangedAt] DATETIME2 (7) NOT NULL);");
        sb.AppendLine();
    }

    sb.AppendLine("    INSERT INTO [audit].[ChangeLog]");
    sb.AppendLine("        ([TableName], [Operation], [EntityId], [DocumentId], [DocumentVersionId], [LogicalNodeId], [OldValues], [NewValues],");
    sb.AppendLine("         [ChangedColumns], [UserId], [Source], [DbLogin], [AppName], [CorrelationId], [OperationContext], [Ticket], [Reason])");
    if (needsLogTable)
    {
        sb.AppendLine("    OUTPUT INSERTED.[Id], INSERTED.[EntityId], INSERTED.[ChangedAt] INTO @Logged ([Id], [EntityId], [ChangedAt])");
    }

    sb.AppendLine("    SELECT");
    sb.AppendLine(CultureInfo.InvariantCulture, $"        N'app.{t.Name}',");
    sb.AppendLine(CultureInfo.InvariantCulture, $"        CASE WHEN [d].[{key}] IS NULL THEN 'I' WHEN [i].[{key}] IS NULL THEN 'D' ELSE 'U' END,");
    sb.AppendLine(CultureInfo.InvariantCulture, $"        {r},");
    sb.AppendLine(CultureInfo.InvariantCulture, $"        {documentExpression},");
    sb.AppendLine(CultureInfo.InvariantCulture, $"        {versionExpression},");
    sb.AppendLine(CultureInfo.InvariantCulture, $"        {Resolve(t.LogicalNodeId)},");
    sb.AppendLine(CultureInfo.InvariantCulture, $"        CASE WHEN [d].[{key}] IS NULL THEN NULL ELSE {JsonOf("d", key, logged)} END,");
    sb.AppendLine(CultureInfo.InvariantCulture, $"        CASE WHEN [i].[{key}] IS NULL THEN NULL ELSE {JsonOf("i", key, logged)} END,");
    sb.AppendLine(CultureInfo.InvariantCulture, $"        CASE WHEN [i].[{key}] IS NOT NULL AND [d].[{key}] IS NOT NULL");
    sb.AppendLine("             THEN NULLIF(CONCAT_WS(N',',");
    sb.AppendLine(CultureInfo.InvariantCulture, $"                   {changedList}), N'')");
    sb.AppendLine("        END,");
    sb.AppendLine("        [ctx].[UserId], [ctx].[Source], [ctx].[DbLogin], [ctx].[AppName], [ctx].[CorrelationId], [ctx].[OperationContext], [ctx].[Ticket], [ctx].[Reason]");
    sb.AppendLine("    FROM inserted AS [i]");
    sb.AppendLine(CultureInfo.InvariantCulture, $"    FULL OUTER JOIN deleted AS [d] ON [d].[{key}] = [i].[{key}]");
    sb.AppendLine("    CROSS JOIN [audit].[fn_ChangeContext]() AS [ctx]");
    sb.AppendLine(CultureInfo.InvariantCulture, $"    WHERE [d].[{key}] IS NULL OR [i].[{key}] IS NULL");
    sb.AppendLine(CultureInfo.InvariantCulture, $"       OR {changedAny}");
    if (t.DerivedColumns.Length > 0)
    {
        sb.AppendLine(CultureInfo.InvariantCulture, $"       OR ([ctx].[Source] = 'Script' AND ({derivedAny}))");
    }

    sb.AppendLine("    ;");

    if (t.AdvancesVersionStamp)
    {
        var tamper = t.TamperCondition is null ? "CAST(NULL AS DATETIME2 (7))" : $"MIN(CASE WHEN {t.TamperCondition} THEN [l].[ChangedAt] END)";
        var versionOf = (string alias) => t.DocumentVersionId!.Replace("{r}", alias, StringComparison.Ordinal);
        sb.AppendLine();
        sb.AppendLine("    -- T03 §2b: advance the per-version stamp (cache key/ETag) of every version the change touches — old and new version");
        sb.AppendLine("    -- for rows that move between versions — and record the first tampering with a Signed version (TamperedAt = ChangedAt of that log row).");
        sb.AppendLine("    MERGE [app].[VersionStamp] WITH (HOLDLOCK) AS [target]");
        sb.AppendLine("    USING (");
        var content = t.ChangesContent ? "MAX([l].[Id])" : "CAST(NULL AS BIGINT)";
        sb.AppendLine(CultureInfo.InvariantCulture, $"        SELECT [x].[DocumentVersionId], MAX([l].[Id]) AS [LastChangeLogId], {content} AS [ContentChangeLogId], MAX([l].[ChangedAt]) AS [LastChangedAt],");
        sb.AppendLine(CultureInfo.InvariantCulture, $"               {tamper} AS [TamperedAt]");
        sb.AppendLine("        FROM @Logged AS [l]");
        sb.AppendLine(CultureInfo.InvariantCulture, $"        LEFT JOIN inserted AS [i] ON [i].[{key}] = [l].[EntityId]");
        sb.AppendLine(CultureInfo.InvariantCulture, $"        LEFT JOIN deleted AS [d] ON [d].[{key}] = [l].[EntityId]");
        sb.AppendLine(CultureInfo.InvariantCulture, $"        CROSS APPLY (VALUES ({versionOf("[i]")}), ({versionOf("[d]")})) AS [x] ([DocumentVersionId])");
        sb.AppendLine("        JOIN [app].[DocumentVersion] AS [v] ON [v].[Id] = [x].[DocumentVersionId]");
        sb.AppendLine("        CROSS JOIN [audit].[fn_ChangeContext]() AS [ctx]");
        sb.AppendLine("        GROUP BY [x].[DocumentVersionId]) AS [source]");
        sb.AppendLine("    ON [target].[DocumentVersionId] = [source].[DocumentVersionId]");
        sb.AppendLine("    WHEN MATCHED THEN");
        sb.AppendLine("        UPDATE SET [LastChangeLogId] = CASE WHEN [source].[LastChangeLogId] > [target].[LastChangeLogId] THEN [source].[LastChangeLogId] ELSE [target].[LastChangeLogId] END,");
        sb.AppendLine("                   [ContentChangeLogId] = CASE WHEN [source].[ContentChangeLogId] > [target].[ContentChangeLogId] OR [target].[ContentChangeLogId] IS NULL THEN COALESCE([source].[ContentChangeLogId], [target].[ContentChangeLogId]) ELSE [target].[ContentChangeLogId] END,");
        sb.AppendLine("                   [LastChangedAt] = CASE WHEN [source].[LastChangedAt] > [target].[LastChangedAt] OR [target].[LastChangedAt] IS NULL THEN [source].[LastChangedAt] ELSE [target].[LastChangedAt] END,");
        sb.AppendLine("                   [TamperedAt] = COALESCE([target].[TamperedAt], [source].[TamperedAt])");
        sb.AppendLine("    WHEN NOT MATCHED BY TARGET THEN");
        sb.AppendLine("        INSERT ([DocumentVersionId], [LastChangeLogId], [ContentChangeLogId], [LastChangedAt], [TamperedAt])");
        sb.AppendLine("        VALUES ([source].[DocumentVersionId], [source].[LastChangeLogId], [source].[ContentChangeLogId], [source].[LastChangedAt], [source].[TamperedAt]);");
    }

    sb.AppendLine("END;");
    return sb.ToString();
}

// Null-safe, case- and trailing-space-sensitive comparison (the database collation is case-insensitive).
static string Distinct(AuditedColumn c) => c.IsString
    ? $"CAST([i].[{c.Name}] AS VARBINARY (MAX)) IS DISTINCT FROM CAST([d].[{c.Name}] AS VARBINARY (MAX))"
    : $"[i].[{c.Name}] IS DISTINCT FROM [d].[{c.Name}]";

static string JsonOf(string alias, string key, IEnumerable<AuditedColumn> columns)
{
    var select = string.Join(", ", new[] { key }.Concat(columns.Select(c => c.Name)).Select(n => $"[{alias}].[{n}] AS [{n}]"));
    return $"(SELECT {select} FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES)";
}

static string GeneratePartitioning(DateOnly first, int months)
{
    var boundaries = Enumerable.Range(0, months)
        .Select(m => first.AddMonths(m).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
        .Select(d => $"    '{d}T00:00:00'");

    var sb = new StringBuilder();
    sb.AppendLine("-- <auto-generated> by database/tools/GenerateAuditTriggers.cs — do not edit by hand. </auto-generated>");
    sb.AppendLine(CultureInfo.InvariantCulture, $"-- Monthly partitions for audit.ChangeLog, {first:yyyy-MM} to {first.AddMonths(months - 1):yyyy-MM} (NFR-L10). Later rows go to the last partition;");
    sb.AppendLine("-- extend the range by re-running the generator with a later end date (the DACPAC deployment adds the boundaries).");
    sb.AppendLine("CREATE PARTITION FUNCTION [pf_ChangeLog_Month] (DATETIME2 (7))");
    sb.AppendLine("AS RANGE RIGHT FOR VALUES (");
    sb.AppendLine(string.Join(",\n", boundaries));
    sb.AppendLine(");");
    sb.AppendLine("GO");
    sb.AppendLine("CREATE PARTITION SCHEME [ps_ChangeLog_Month]");
    sb.AppendLine("AS PARTITION [pf_ChangeLog_Month] ALL TO ([PRIMARY]);");
    return sb.ToString();
}

// The ledger allow-list (T21 §4, rule 5): every ledger table of the project and its columns, including the columns SQL
// Server adds to ledger tables. Read from the table scripts, so a reviewed schema change regenerates it.
static List<(string Schema, string Table, string Column)> ReadLedgerAllowList(string project)
{
    var list = new List<(string, string, string)>();
    foreach (var file in Directory.GetFiles(project, "*.sql", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
    {
        var relative = Path.GetRelativePath(project, file);
        if (relative.StartsWith("bin", StringComparison.Ordinal) || relative.StartsWith("obj", StringComparison.Ordinal))
        {
            continue;
        }

        var text = File.ReadAllText(file);
        var table = Regex.Match(text, @"CREATE TABLE \[(\w+)\]\.\[(\w+)\]");
        if (!table.Success || !text.Contains("LEDGER = ON", StringComparison.Ordinal))
        {
            continue;
        }

        var body = text[table.Index..];
        // Column lines end at the first constraint or the closing parenthesis of CREATE TABLE.
        var end = Regex.Match(body, @"^\s*(CONSTRAINT|PERIOD|\))", RegexOptions.Multiline);
        var columns = Regex.Matches(end.Success ? body[..end.Index] : body, @"^\s+\[(\w+)\]\s+[A-Z]", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value)
            .ToList();
        columns.AddRange(["ledger_start_transaction_id", "ledger_start_sequence_number"]);
        if (!text.Contains("APPEND_ONLY = ON", StringComparison.Ordinal))
        {
            columns.AddRange(["ledger_end_transaction_id", "ledger_end_sequence_number"]);
        }

        list.AddRange(columns.Distinct(StringComparer.Ordinal).Select(c => (table.Groups[1].Value, table.Groups[2].Value, c)));
    }

    return list;
}

static string GenerateReconciliation(AuditedTable[] tables, List<(string Schema, string Table, string Column)> allowList)
{
    var sb = new StringBuilder();
    sb.Append(CultureInfo.InvariantCulture, $$"""
        -- <auto-generated> by database/tools/GenerateAuditTriggers.cs — do not edit by hand. </auto-generated>
        /*
        Ledger reconciliation (T21 §4). Compares the ledger history of the audited tables with audit.ChangeLog, using the change
        rules of the audit triggers, and records findings (rules 1–5; rule 6, module integrity, is checked by the API):
          1 TriggerBypass    a ledger change the triggers would have audited, without a ChangeLog row of the same transaction;
          2 ForgedAuditRow   a ChangeLog row its ledger transaction doesn't back (entity not changed, Source/DbLogin not matching
                             the transaction principal);
          3 StampTampering   a VersionStamp change not caused by an audited change of the version (or clearing TamperedAt);
          4 LedgerIntegrity  sp_verify_database_ledger(_from_digest_storage) failed;
          5 SchemaTampering  a ledger table/column dropped, renamed or transferred, or created outside the allow-list (all time).
        Transactions older than the first ReconciliationBaseline row are ignored (rules 1–3). A finding is recorded once; for each
        affected version the procedure writes a ChangeLog row (OperationContext 'Reconciliation') and advances VersionStamp, sets
        TamperedAt of a Signed version when the finding's transaction committed at or after the version's ledger signing
        transaction, and marks bypassed NodeContent rows DerivedStale.
        EXECUTE AS OWNER: the procedure reads objects by object id through dynamic SQL (renamed/transferred/dropped baseline and
        finding tables), which ownership chaining doesn't cover. Ledger transactions still record the caller's login.
        Must not run inside a transaction (ledger verification refuses user transactions). @Digest: a digest from
        sys.sp_generate_database_ledger_digest, used when no automatic digest storage is configured (local, tests). @To NULL =
        every transaction committed from @From on.
        */
        CREATE PROCEDURE [audit].[usp_ReconcileLedger]
            @From        DATETIME2 (7),
            @To          DATETIME2 (7)  = NULL,
            @Digest      NVARCHAR (MAX) = NULL,
            @NewFindings INT            = NULL OUTPUT
        WITH EXECUTE AS OWNER
        AS
        BEGIN
            SET NOCOUNT ON;
            SET XACT_ABORT ON;

            IF @@TRANCOUNT > 0
                THROW 50101, N'Ledger reconciliation must not run inside a transaction.', 1;

            IF @From IS NULL
                THROW 50102, N'@From is required.', 1;

            CREATE TABLE [#Finding]
            (
                [Key]                 INT IDENTITY (1, 1) PRIMARY KEY,
                [Kind]                VARCHAR (40) COLLATE DATABASE_DEFAULT NOT NULL,
                [TableName]           NVARCHAR (256) COLLATE DATABASE_DEFAULT NULL,
                [EntityId]            INT             NULL,
                [DocumentId]          INT             NULL,
                [DocumentVersionId]   INT             NULL,
                [LedgerTransactionId] BIGINT          NULL,
                [Detail]              NVARCHAR (1000) COLLATE DATABASE_DEFAULT NULL,
                [MarksDerivedStale]   BIT             NOT NULL DEFAULT (0)
            );

            -- Rule 4 — ledger integrity, first and outside any transaction.
            DECLARE @IntegrityError NVARCHAR (1000);
            DECLARE @Locations NVARCHAR (MAX) = (SELECT * FROM sys.database_ledger_digest_locations FOR JSON AUTO, INCLUDE_NULL_VALUES);
            BEGIN TRY
                IF @Locations IS NOT NULL
                    EXEC sys.sp_verify_database_ledger_from_digest_storage @Locations;
                ELSE IF @Digest IS NOT NULL
                    EXEC sys.sp_verify_database_ledger @Digest;
            END TRY
            BEGIN CATCH
                SET @IntegrityError = LEFT(CONCAT(N'Msg ', ERROR_NUMBER(), N': ', ERROR_MESSAGE()), 1000);
            END CATCH;

            IF @IntegrityError IS NOT NULL
                INSERT INTO [#Finding] ([Kind], [Detail]) VALUES ('LedgerIntegrity', @IntegrityError);

            -- §2 — every object that ever was audit.ReconciliationBaseline ('B') / audit.ReconciliationFinding ('F'), by object id.
            CREATE TABLE [#Object] ([Role] CHAR (1) COLLATE DATABASE_DEFAULT NOT NULL, [ObjectId] INT NOT NULL, [QualifiedName] NVARCHAR (300) COLLATE DATABASE_DEFAULT NOT NULL);
            INSERT INTO [#Object] ([Role], [ObjectId], [QualifiedName])
            SELECT DISTINCT [r].[Role], [o].[object_id], QUOTENAME(OBJECT_SCHEMA_NAME([o].[object_id])) + N'.' + QUOTENAME([o].[name])
            FROM (VALUES ('B', N'ReconciliationBaseline'), ('F', N'ReconciliationFinding')) AS [r] ([Role], [Name])
            CROSS APPLY (SELECT [h].[object_id] FROM sys.ledger_table_history AS [h] WHERE [h].[schema_name] COLLATE DATABASE_DEFAULT = N'audit' AND [h].[table_name] COLLATE DATABASE_DEFAULT = [r].[Name]
                         UNION
                         SELECT OBJECT_ID(N'[audit].' + QUOTENAME([r].[Name]))) AS [x] ([ObjectId])
            JOIN sys.tables AS [o] ON [o].[object_id] = [x].[ObjectId];

            -- Findings of reconciliation, of every such object (their ChangeLog rows are not forged, rule 2).
            CREATE TABLE [#FindingTx] ([Id] BIGINT NOT NULL, [TransactionId] BIGINT NOT NULL);

            DECLARE @Baseline BIGINT, @Value BIGINT, @Role CHAR (1), @Name NVARCHAR (300), @Current BIT, @Sql NVARCHAR (MAX);
            DECLARE [objects] CURSOR LOCAL FAST_FORWARD FOR
                SELECT [Role], [QualifiedName], CASE WHEN [ObjectId] = OBJECT_ID(N'[audit].[ReconciliationFinding]') THEN 1 ELSE 0 END FROM [#Object];
            OPEN [objects];
            FETCH NEXT FROM [objects] INTO @Role, @Name, @Current;
            WHILE @@FETCH_STATUS = 0
            BEGIN
                -- An object without the expected columns isn't one of ours any more; rule 5 reports what happened to it.
                BEGIN TRY
                    IF @Role = 'B'
                    BEGIN
                        SET @Sql = N'SELECT @Value = MIN([ledger_start_transaction_id]) FROM ' + @Name + N';';
                        EXEC sys.sp_executesql @Sql, N'@Value BIGINT OUTPUT', @Value = @Value OUTPUT;
                        IF @Value IS NOT NULL AND (@Baseline IS NULL OR @Value < @Baseline)
                            SET @Baseline = @Value;
                    END
                    ELSE
                    BEGIN
                        SET @Sql = N'SELECT [Id], [ledger_start_transaction_id] FROM ' + @Name + N';';
                        INSERT INTO [#FindingTx] ([Id], [TransactionId]) EXEC sys.sp_executesql @Sql;
                        IF @Current = 0
                        BEGIN
                            -- Re-import findings of a dropped/renamed/transferred findings table, so they can't be made to vanish.
                            SET @Sql = N'
                            INSERT INTO [audit].[ReconciliationFinding] ([Kind], [TableName], [EntityId], [DocumentId], [DocumentVersionId], [LedgerTransactionId],
                                                                         [TransactionCommitTime], [Principal], [Detail], [DetectedAt], [AfterSigning])
                            SELECT [o].[Kind], [o].[TableName], [o].[EntityId], [o].[DocumentId], [o].[DocumentVersionId], [o].[LedgerTransactionId],
                                   [o].[TransactionCommitTime], [o].[Principal], [o].[Detail], [o].[DetectedAt], [o].[AfterSigning]
                            FROM ' + @Name + N' AS [o]
                            WHERE NOT EXISTS (SELECT 1 FROM [audit].[ReconciliationFinding] AS [f]
                                              WHERE [f].[Kind] = [o].[Kind] AND [f].[TableName] IS NOT DISTINCT FROM [o].[TableName]
                                                AND [f].[EntityId] IS NOT DISTINCT FROM [o].[EntityId]
                                                AND [f].[LedgerTransactionId] IS NOT DISTINCT FROM [o].[LedgerTransactionId]
                                                AND ([f].[LedgerTransactionId] IS NOT NULL OR [f].[Detail] IS NOT DISTINCT FROM [o].[Detail]));';
                            EXEC sys.sp_executesql @Sql;
                        END;
                    END;
                END TRY
                BEGIN CATCH
                END CATCH;

                FETCH NEXT FROM [objects] INTO @Role, @Name, @Current;
            END;
            CLOSE [objects];
            DEALLOCATE [objects];

            -- Ledger transactions of the window, from the baseline on, with their principal. The principal is a login (or external
            -- principal) name; membership is resolved by SID, by user name when the SID can't be resolved.
            CREATE TABLE [#Tx]
            (
                [TransactionId] BIGINT         NOT NULL PRIMARY KEY,
                [CommitTime]    DATETIME2 (7)  NOT NULL,
                [Principal]     NVARCHAR (128) COLLATE DATABASE_DEFAULT NOT NULL,
                [IsApi]         BIT            NOT NULL
            );
            INSERT INTO [#Tx] ([TransactionId], [CommitTime], [Principal], [IsApi])
            SELECT [t].[transaction_id], [t].[commit_time], [t].[principal_name] COLLATE DATABASE_DEFAULT,
                   CASE WHEN EXISTS (SELECT 1
                                     FROM sys.database_role_members AS [m]
                                     JOIN sys.database_principals AS [p] ON [p].[principal_id] = [m].[member_principal_id]
                                     WHERE [m].[role_principal_id] = DATABASE_PRINCIPAL_ID(N'app_api')
                                       AND ([p].[sid] = SUSER_SID([t].[principal_name])
                                            OR (SUSER_SID([t].[principal_name]) IS NULL AND [p].[name] = [t].[principal_name])))
                        THEN 1 ELSE 0 END
            FROM sys.database_ledger_transactions AS [t]
            -- No upper bound unless @To is given: commit_time can run ahead of SYSUTCDATETIME() by milliseconds, so "until now"
            -- must not exclude just-committed transactions.
            WHERE [t].[commit_time] >= @From AND (@To IS NULL OR [t].[commit_time] <= @To)
              AND [t].[transaction_id] >= COALESCE(@Baseline, 0);

            -- Every ledger change of an audited table in the window: the INSERT (after) and DELETE (before) images of one key in
            -- one transaction are paired in sequence order. Audited = what the trigger rules log: insert, delete, an audited column
            -- changed (strings as VARBINARY), or a derived NodeContent column changed by a principal that isn't the API.
            CREATE TABLE [#Change]
            (
                [TableName]     NVARCHAR (128) COLLATE DATABASE_DEFAULT NOT NULL,
                [EntityId]      INT            NOT NULL,
                [TransactionId] BIGINT         NOT NULL,
                [DocumentId]    INT            NULL,
                [VersionNew]    INT            NULL,
                [VersionOld]    INT            NULL,
                [Audited]       BIT            NOT NULL
            );

        """);

    foreach (var t in tables)
    {
        var key = t.KeyColumn;
        string Of(string? template, string alias) => template is null ? "NULL" : template.Replace("{r}", alias, StringComparison.Ordinal);
        var versionNew = t.AdvancesVersionStamp ? Of(t.DocumentVersionId, "[i]") : "NULL";
        var versionOld = t.AdvancesVersionStamp ? Of(t.DocumentVersionId, "[d]") : "NULL";
        var anyVersion = t.DocumentVersionId is null ? null : $"COALESCE({Of(t.DocumentVersionId, "[i]")}, {Of(t.DocumentVersionId, "[d]")})";
        var document = t.DocumentId is not null
            ? $"COALESCE({Of(t.DocumentId, "[i]")}, {Of(t.DocumentId, "[d]")})"
            : anyVersion is not null
                ? $"(SELECT [v].[DocumentId] FROM [app].[DocumentVersion] AS [v] WHERE [v].[Id] = {anyVersion})"
                : "NULL";
        var changed = string.Join("\n                     OR ", t.Columns.Select(Distinct));
        var derived = t.DerivedColumns.Length == 0 ? "" : $"\n                     OR ([t].[IsApi] = 0 AND ({string.Join(" OR ", t.DerivedColumns.Select(Distinct))}))";

        sb.Append(CultureInfo.InvariantCulture, $$"""
                WITH [l] AS (
                    SELECT [x].*, ROW_NUMBER() OVER (PARTITION BY [x].[ledger_transaction_id], [x].[{{key}}], [x].[ledger_operation_type] ORDER BY [x].[ledger_sequence_number]) AS [n]
                    FROM [app].[{{t.Name}}_Ledger] AS [x]
                    JOIN [#Tx] AS [tx] ON [tx].[TransactionId] = [x].[ledger_transaction_id])
                INSERT INTO [#Change] ([TableName], [EntityId], [TransactionId], [DocumentId], [VersionNew], [VersionOld], [Audited])
                SELECT N'app.{{t.Name}}', COALESCE([i].[{{key}}], [d].[{{key}}]), [t].[TransactionId],
                       {{document}},
                       {{versionNew}}, {{versionOld}},
                       CASE WHEN [i].[{{key}}] IS NULL OR [d].[{{key}}] IS NULL
                     OR {{changed}}{{derived}}
                            THEN 1 ELSE 0 END
                FROM (SELECT * FROM [l] WHERE [l].[ledger_operation_type] = 1) AS [i]
                FULL OUTER JOIN (SELECT * FROM [l] WHERE [l].[ledger_operation_type] = 2) AS [d]
                    ON [d].[ledger_transaction_id] = [i].[ledger_transaction_id] AND [d].[{{key}}] = [i].[{{key}}] AND [d].[n] = [i].[n]
                JOIN [#Tx] AS [t] ON [t].[TransactionId] = COALESCE([i].[ledger_transaction_id], [d].[ledger_transaction_id]);


            """);
    }

    var audited = string.Join(", ", tables.Select(t => $"N'app.{t.Name}'"));
    var allowed = string.Join(",\n        ", allowList.Select(a => $"(N'{a.Schema}', N'{a.Table}', N'{a.Column}')"));
    sb.Append(CultureInfo.InvariantCulture, $$"""
            -- ChangeLog rows written in the window (hidden ledger column: written by SQL Server, immutable).
            CREATE TABLE [#Log]
            (
                [Id]                BIGINT         NOT NULL,
                [TransactionId]     BIGINT         NOT NULL,
                [TableName]         NVARCHAR (128) COLLATE DATABASE_DEFAULT NOT NULL,
                [Operation]         CHAR (1) COLLATE DATABASE_DEFAULT NOT NULL,
                [EntityId]          INT            NOT NULL,
                [DocumentId]        INT            NULL,
                [DocumentVersionId] INT            NULL,
                [OldVersionId]      INT            NULL,
                [Source]            VARCHAR (10) COLLATE DATABASE_DEFAULT NOT NULL,
                [DbLogin]           NVARCHAR (128) COLLATE DATABASE_DEFAULT NOT NULL
            );
            INSERT INTO [#Log] ([Id], [TransactionId], [TableName], [Operation], [EntityId], [DocumentId], [DocumentVersionId], [OldVersionId], [Source], [DbLogin])
            SELECT [cl].[Id], [cl].[ledger_start_transaction_id], [cl].[TableName], [cl].[Operation], [cl].[EntityId], [cl].[DocumentId], [cl].[DocumentVersionId],
                   TRY_CONVERT(INT, JSON_VALUE([cl].[OldValues], N'$.DocumentVersionId')), [cl].[Source], [cl].[DbLogin]
            FROM [audit].[ChangeLog] AS [cl]
            JOIN [#Tx] AS [t] ON [t].[TransactionId] = [cl].[ledger_start_transaction_id];

            -- Rule 1 — trigger bypass.
            INSERT INTO [#Finding] ([Kind], [TableName], [EntityId], [DocumentId], [DocumentVersionId], [LedgerTransactionId], [Detail], [MarksDerivedStale])
            SELECT 'TriggerBypass', [c].[TableName], [c].[EntityId], MAX([c].[DocumentId]), MAX(COALESCE([c].[VersionNew], [c].[VersionOld])), [c].[TransactionId],
                   N'Ledger change without an audit row of the same transaction (trigger disabled or bypassed).',
                   CASE WHEN [c].[TableName] = N'app.NodeContent' THEN 1 ELSE 0 END
            FROM [#Change] AS [c]
            WHERE [c].[Audited] = 1
              AND NOT EXISTS (SELECT 1 FROM [#Log] AS [cl]
                              WHERE [cl].[TransactionId] = [c].[TransactionId] AND [cl].[TableName] = [c].[TableName] AND [cl].[EntityId] = [c].[EntityId])
            GROUP BY [c].[TableName], [c].[EntityId], [c].[TransactionId];

            -- Rule 2 — forged audit rows.
            INSERT INTO [#Finding] ([Kind], [TableName], [EntityId], [DocumentId], [DocumentVersionId], [LedgerTransactionId], [Detail])
            SELECT 'ForgedAuditRow', [g].[TableName], [g].[EntityId], MAX([g].[DocumentId]), MAX([g].[DocumentVersionId]), [g].[TransactionId],
                   LEFT(STRING_AGG(CONCAT(N'ChangeLog ', [g].[Id], N': ', [g].[Reason]), N'; '), 1000)
            FROM (SELECT [cl].[Id], [cl].[TableName], [cl].[EntityId], [cl].[DocumentId], [cl].[TransactionId],
                         CASE WHEN [cl].[TableName] IN (N'app.DocumentVersion', N'app.VersionSignature', N'app.DocumentNode', N'app.NodeContent')
                              THEN [cl].[DocumentVersionId] END AS [DocumentVersionId],
                         CASE
                             WHEN [cl].[TableName] = N'audit.ReconciliationFinding' THEN
                                 CASE WHEN [cl].[Operation] = 'I'
                                           AND EXISTS (SELECT 1 FROM [#FindingTx] AS [f] WHERE [f].[Id] = [cl].[EntityId] AND [f].[TransactionId] = [cl].[TransactionId])
                                      THEN NULL
                                      ELSE N'reconciliation row without a finding inserted in the same transaction' END
                             WHEN [cl].[TableName] NOT IN ({{audited}}) THEN N'not an audited table'
                             WHEN NOT EXISTS (SELECT 1 FROM [#Change] AS [c]
                                              WHERE [c].[TableName] = [cl].[TableName] AND [c].[EntityId] = [cl].[EntityId] AND [c].[TransactionId] = [cl].[TransactionId])
                                 THEN N'its ledger transaction did not change the entity'
                             WHEN [cl].[Source] = 'App' AND [t].[IsApi] = 0
                                 THEN CONCAT(N'Source = App, but the transaction principal ', [t].[Principal], N' is not an app_api member')
                             WHEN [cl].[Source] = 'Script' AND NOT ([cl].[DbLogin] = [t].[Principal] OR SUSER_SID([cl].[DbLogin]) = SUSER_SID([t].[Principal]))
                                 THEN CONCAT(N'DbLogin ', [cl].[DbLogin], N' is not the transaction principal ', [t].[Principal])
                         END AS [Reason]
                  FROM [#Log] AS [cl]
                  JOIN [#Tx] AS [t] ON [t].[TransactionId] = [cl].[TransactionId]) AS [g]
            WHERE [g].[Reason] IS NOT NULL
            GROUP BY [g].[TableName], [g].[EntityId], [g].[TransactionId];

            -- Rule 3 — stamp tampering: a VersionStamp change is legitimate only together with an audited change of the version in
            -- the same transaction (the triggers' MERGE, or reconciliation's own ChangeLog row), never clearing or moving TamperedAt
            -- and never moving LastChangeLogId back. Identical before/after images (no-op updates) are ignored.
            WITH [l] AS (
                SELECT [x].*, ROW_NUMBER() OVER (PARTITION BY [x].[ledger_transaction_id], [x].[DocumentVersionId], [x].[ledger_operation_type] ORDER BY [x].[ledger_sequence_number]) AS [n]
                FROM [app].[VersionStamp_Ledger] AS [x]
                JOIN [#Tx] AS [tx] ON [tx].[TransactionId] = [x].[ledger_transaction_id])
            INSERT INTO [#Finding] ([Kind], [TableName], [EntityId], [DocumentId], [DocumentVersionId], [LedgerTransactionId], [Detail])
            SELECT 'StampTampering', N'app.VersionStamp', [s].[VersionId], MAX([v].[DocumentId]), [s].[VersionId], [s].[TransactionId],
                   N'VersionStamp changed without an audited change of the version, or TamperedAt cleared/moved.'
            FROM (SELECT COALESCE([i].[DocumentVersionId], [d].[DocumentVersionId]) AS [VersionId],
                         COALESCE([i].[ledger_transaction_id], [d].[ledger_transaction_id]) AS [TransactionId],
                         CASE WHEN ([i].[DocumentVersionId] IS NULL OR [d].[DocumentVersionId] IS NULL
                                    OR (([d].[TamperedAt] IS NULL OR [i].[TamperedAt] = [d].[TamperedAt]) AND [i].[LastChangeLogId] >= [d].[LastChangeLogId]))
                                   AND EXISTS (SELECT 1 FROM [#Log] AS [cl]
                                               WHERE [cl].[TransactionId] = COALESCE([i].[ledger_transaction_id], [d].[ledger_transaction_id])
                                                 AND COALESCE([i].[DocumentVersionId], [d].[DocumentVersionId]) IN ([cl].[DocumentVersionId], [cl].[OldVersionId]))
                              THEN 1 ELSE 0 END AS [Legitimate],
                         CASE WHEN [i].[DocumentVersionId] IS NOT NULL AND [d].[DocumentVersionId] IS NOT NULL
                                   AND [i].[LastChangeLogId] = [d].[LastChangeLogId] AND [i].[TamperedAt] IS NOT DISTINCT FROM [d].[TamperedAt]
                              THEN 1 ELSE 0 END AS [NoOp]
                  FROM (SELECT * FROM [l] WHERE [l].[ledger_operation_type] = 1) AS [i]
                  FULL OUTER JOIN (SELECT * FROM [l] WHERE [l].[ledger_operation_type] = 2) AS [d]
                      ON [d].[ledger_transaction_id] = [i].[ledger_transaction_id] AND [d].[DocumentVersionId] = [i].[DocumentVersionId] AND [d].[n] = [i].[n]) AS [s]
            LEFT JOIN [app].[DocumentVersion] AS [v] ON [v].[Id] = [s].[VersionId]
            WHERE [s].[Legitimate] = 0 AND [s].[NoOp] = 0
            GROUP BY [s].[VersionId], [s].[TransactionId];

            -- Rule 5 — ledger schema tampering, over all time, by object id. The allow-list is the ledger tables and columns of the
            -- DACPAC model (generated from the table scripts).
            CREATE TABLE [#Allowed] ([SchemaName] SYSNAME COLLATE DATABASE_DEFAULT NOT NULL, [TableName] SYSNAME COLLATE DATABASE_DEFAULT NOT NULL, [ColumnName] SYSNAME COLLATE DATABASE_DEFAULT NOT NULL);
            INSERT INTO [#Allowed] ([SchemaName], [TableName], [ColumnName])
            VALUES
                {{allowed}};

            WITH [created] AS (
                SELECT [h].[object_id], MIN(CONCAT([h].[schema_name] COLLATE DATABASE_DEFAULT, N'.', [h].[table_name] COLLATE DATABASE_DEFAULT)) AS [Name], MIN([h].[schema_name] COLLATE DATABASE_DEFAULT) AS [SchemaName], MIN([h].[table_name] COLLATE DATABASE_DEFAULT) AS [TableName]
                FROM sys.ledger_table_history AS [h]
                WHERE [h].[operation_type_desc] COLLATE DATABASE_DEFAULT = N'CREATE'
                GROUP BY [h].[object_id])
            INSERT INTO [#Finding] ([Kind], [TableName], [EntityId], [LedgerTransactionId], [Detail])
            SELECT 'SchemaTampering', COALESCE([c].[Name], MIN(CONCAT([h].[schema_name] COLLATE DATABASE_DEFAULT, N'.', [h].[table_name] COLLATE DATABASE_DEFAULT))), NULL, [h].[transaction_id],
                   LEFT(STRING_AGG(CONCAT(N'Table ', [h].[operation_type_desc] COLLATE DATABASE_DEFAULT, N': ', [h].[schema_name] COLLATE DATABASE_DEFAULT, N'.', [h].[table_name] COLLATE DATABASE_DEFAULT), N'; '), 1000)
            FROM sys.ledger_table_history AS [h]
            LEFT JOIN [created] AS [c] ON [c].[object_id] = [h].[object_id]
            WHERE [h].[operation_type_desc] COLLATE DATABASE_DEFAULT <> N'CREATE'
               OR NOT EXISTS (SELECT 1 FROM [#Allowed] AS [a] WHERE [a].[SchemaName] = [h].[schema_name] COLLATE DATABASE_DEFAULT AND [a].[TableName] = [h].[table_name] COLLATE DATABASE_DEFAULT)
            GROUP BY [h].[object_id], [c].[Name], [h].[transaction_id];

            WITH [created] AS (
                SELECT [h].[object_id], MIN([h].[schema_name] COLLATE DATABASE_DEFAULT) AS [SchemaName], MIN([h].[table_name] COLLATE DATABASE_DEFAULT) AS [TableName]
                FROM sys.ledger_table_history AS [h]
                WHERE [h].[operation_type_desc] COLLATE DATABASE_DEFAULT = N'CREATE'
                GROUP BY [h].[object_id])
            INSERT INTO [#Finding] ([Kind], [TableName], [EntityId], [LedgerTransactionId], [Detail])
            SELECT 'SchemaTampering', CONCAT([c].[SchemaName], N'.', [c].[TableName]), [h].[column_id], [h].[transaction_id],
                   LEFT(STRING_AGG(CONCAT(N'Column ', [h].[operation_type_desc] COLLATE DATABASE_DEFAULT, N': ', [h].[column_name] COLLATE DATABASE_DEFAULT), N'; '), 1000)
            FROM sys.ledger_column_history AS [h]
            JOIN [created] AS [c] ON [c].[object_id] = [h].[object_id]
            WHERE [h].[operation_type_desc] COLLATE DATABASE_DEFAULT NOT IN (N'CREATE', N'ADD')
               OR (EXISTS (SELECT 1 FROM [#Allowed] AS [a] WHERE [a].[SchemaName] = [c].[SchemaName] AND [a].[TableName] = [c].[TableName])
                   AND NOT EXISTS (SELECT 1 FROM [#Allowed] AS [a]
                                   WHERE [a].[SchemaName] = [c].[SchemaName] AND [a].[TableName] = [c].[TableName] AND [a].[ColumnName] = [h].[column_name] COLLATE DATABASE_DEFAULT))
            GROUP BY [c].[SchemaName], [c].[TableName], [h].[column_id], [h].[transaction_id];

            -- Versions a finding affects: its own, plus old and new version of bypassed rows that moved between versions.
            CREATE TABLE [#FindingVersion] ([Key] INT NOT NULL, [VersionId] INT NOT NULL, PRIMARY KEY ([Key], [VersionId]));
            INSERT INTO [#FindingVersion] ([Key], [VersionId])
            SELECT [f].[Key], [f].[DocumentVersionId] FROM [#Finding] AS [f] WHERE [f].[DocumentVersionId] IS NOT NULL
            UNION
            SELECT [f].[Key], [x].[VersionId]
            FROM [#Finding] AS [f]
            JOIN [#Change] AS [c] ON [f].[Kind] = 'TriggerBypass' AND [c].[TableName] = [f].[TableName] AND [c].[EntityId] = [f].[EntityId] AND [c].[TransactionId] = [f].[LedgerTransactionId]
            CROSS APPLY (VALUES ([c].[VersionNew]), ([c].[VersionOld])) AS [x] ([VersionId])
            WHERE [x].[VersionId] IS NOT NULL;

            -- Ledger signing time: commit time of the first transaction that made the version Signed (not the editable SignedAt).
            CREATE TABLE [#Signing] ([VersionId] INT NOT NULL PRIMARY KEY, [CommitTime] DATETIME2 (7) NOT NULL);
            INSERT INTO [#Signing] ([VersionId], [CommitTime])
            SELECT [l].[Id], MIN([t].[commit_time])
            FROM [app].[DocumentVersion_Ledger] AS [l]
            JOIN sys.database_ledger_transactions AS [t] ON [t].[transaction_id] = [l].[ledger_transaction_id]
            WHERE [l].[ledger_operation_type] = 1 AND [l].[Status] = 2 AND [l].[Id] IN (SELECT [VersionId] FROM [#FindingVersion])
            GROUP BY [l].[Id];

            DECLARE @New TABLE ([Key] INT NOT NULL, [Id] BIGINT NOT NULL);
            DECLARE @Logged TABLE ([Id] BIGINT NOT NULL, [FindingId] INT NOT NULL, [VersionId] INT NOT NULL);

            BEGIN TRANSACTION;

            -- One run at a time, so the duplicate check below sees every finding.
            EXEC sys.sp_getapplock @Resource = N'audit.usp_ReconcileLedger', @LockMode = 'Exclusive', @LockOwner = 'Transaction';

            -- Append-only tables allow INSERT only; new findings are mapped back to #Finding by their identity (unique per run).
            DECLARE @Inserted TABLE ([Id] BIGINT NOT NULL, [Kind] VARCHAR (40) NOT NULL, [TableName] NVARCHAR (256) NULL, [EntityId] INT NULL,
                                     [LedgerTransactionId] BIGINT NULL, [Detail] NVARCHAR (1000) NULL);
            INSERT INTO [audit].[ReconciliationFinding]
                ([Kind], [TableName], [EntityId], [DocumentId], [DocumentVersionId], [LedgerTransactionId], [TransactionCommitTime], [Principal], [Detail], [AfterSigning])
            OUTPUT INSERTED.[Id], INSERTED.[Kind], INSERTED.[TableName], INSERTED.[EntityId], INSERTED.[LedgerTransactionId], INSERTED.[Detail] INTO @Inserted
            SELECT [f].[Kind], [f].[TableName], [f].[EntityId], [f].[DocumentId], [f].[DocumentVersionId], [f].[LedgerTransactionId],
                   [t].[commit_time], [t].[principal_name] COLLATE DATABASE_DEFAULT, [f].[Detail],
                   CASE WHEN [s].[CommitTime] IS NOT NULL AND [t].[commit_time] >= [s].[CommitTime] THEN 1 ELSE 0 END
            FROM [#Finding] AS [f]
            LEFT JOIN sys.database_ledger_transactions AS [t] ON [t].[transaction_id] = [f].[LedgerTransactionId]
            LEFT JOIN [#Signing] AS [s] ON [s].[VersionId] = [f].[DocumentVersionId]
            WHERE NOT EXISTS (SELECT 1 FROM [audit].[ReconciliationFinding] AS [e]
                              WHERE [e].[Kind] = [f].[Kind] AND [e].[TableName] IS NOT DISTINCT FROM [f].[TableName]
                                AND [e].[EntityId] IS NOT DISTINCT FROM [f].[EntityId]
                                AND [e].[LedgerTransactionId] IS NOT DISTINCT FROM [f].[LedgerTransactionId]
                                AND ([e].[LedgerTransactionId] IS NOT NULL OR [e].[Detail] IS NOT DISTINCT FROM [f].[Detail]));

            INSERT INTO @New ([Key], [Id])
            SELECT [f].[Key], [i].[Id]
            FROM @Inserted AS [i]
            JOIN [#Finding] AS [f]
                ON [f].[Kind] = [i].[Kind] AND [f].[TableName] IS NOT DISTINCT FROM [i].[TableName] AND [f].[EntityId] IS NOT DISTINCT FROM [i].[EntityId]
               AND [f].[LedgerTransactionId] IS NOT DISTINCT FROM [i].[LedgerTransactionId] AND [f].[Detail] IS NOT DISTINCT FROM [i].[Detail];

            -- Effects, per affected (still existing) version: a ChangeLog row, VersionStamp advanced, TamperedAt for Signed versions.
            INSERT INTO [audit].[ChangeLog]
                ([TableName], [Operation], [EntityId], [DocumentId], [DocumentVersionId], [NewValues], [UserId], [Source], [DbLogin], [AppName],
                 [CorrelationId], [OperationContext])
            OUTPUT INSERTED.[Id], INSERTED.[EntityId], INSERTED.[DocumentVersionId] INTO @Logged ([Id], [FindingId], [VersionId])
            SELECT N'audit.ReconciliationFinding', 'I', CAST([n].[Id] AS INT), [v].[DocumentId], [v].[Id],
                   (SELECT [n].[Id] AS [Id], [f].[Kind] AS [Kind], [f].[TableName] AS [TableName], [f].[EntityId] AS [EntityId],
                           [f].[LedgerTransactionId] AS [LedgerTransactionId]
                    FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
                   [ctx].[UserId], 'App', [ctx].[DbLogin], [ctx].[AppName], [ctx].[CorrelationId], N'Reconciliation'
            FROM @New AS [n]
            JOIN [#Finding] AS [f] ON [f].[Key] = [n].[Key]
            JOIN [#FindingVersion] AS [fv] ON [fv].[Key] = [f].[Key]
            JOIN [app].[DocumentVersion] AS [v] ON [v].[Id] = [fv].[VersionId]
            CROSS JOIN [audit].[fn_ChangeContext]() AS [ctx];

            MERGE [app].[VersionStamp] WITH (HOLDLOCK) AS [target]
            USING (SELECT [l].[VersionId], MAX([l].[Id]) AS [LastChangeLogId],
                          MIN(CASE WHEN [v].[Status] = 2 AND [t].[commit_time] >= [s].[CommitTime] THEN [t].[commit_time] END) AS [TamperedAt]
                   FROM @Logged AS [l]
                   JOIN @New AS [n] ON [n].[Id] = [l].[FindingId]
                   JOIN [#Finding] AS [f] ON [f].[Key] = [n].[Key]
                   JOIN [app].[DocumentVersion] AS [v] ON [v].[Id] = [l].[VersionId]
                   LEFT JOIN sys.database_ledger_transactions AS [t] ON [t].[transaction_id] = [f].[LedgerTransactionId]
                   LEFT JOIN [#Signing] AS [s] ON [s].[VersionId] = [l].[VersionId]
                   GROUP BY [l].[VersionId]) AS [source]
            ON [target].[DocumentVersionId] = [source].[VersionId]
            WHEN MATCHED THEN
                UPDATE SET [LastChangeLogId] = CASE WHEN [source].[LastChangeLogId] > [target].[LastChangeLogId] THEN [source].[LastChangeLogId] ELSE [target].[LastChangeLogId] END,
                           [TamperedAt] = COALESCE([target].[TamperedAt], [source].[TamperedAt])
            WHEN NOT MATCHED BY TARGET THEN
                INSERT ([DocumentVersionId], [LastChangeLogId], [TamperedAt])
                VALUES ([source].[VersionId], [source].[LastChangeLogId], [source].[TamperedAt]);

            -- Forged ContentHtml/PlainText of bypassed content is re-rendered by the API (T09).
            UPDATE [nc]
            SET [DerivedStale] = 1
            FROM [app].[NodeContent] AS [nc]
            JOIN [#Finding] AS [f] ON [f].[MarksDerivedStale] = 1 AND [f].[EntityId] = [nc].[NodeId]
            JOIN @New AS [n] ON [n].[Key] = [f].[Key]
            WHERE [nc].[DerivedStale] = 0;

            COMMIT TRANSACTION;

            SET @NewFindings = (SELECT COUNT(*) FROM @New);
        END;

        """);
    return sb.ToString();
}

// Rule 6: the name of a module script ([schema].[name] of its CREATE statement).
static string ModuleName(string script)
{
    var match = Regex.Match(script, @"^CREATE\s+(?:PROCEDURE|FUNCTION|VIEW|TRIGGER)\s+\[(\w+)\]\.\[(\w+)\]", RegexOptions.Multiline);
    return match.Success ? $"{match.Groups[1].Value}.{match.Groups[2].Value}" : throw new InvalidOperationException("No module in script.");
}

// Rule 6 normalization — must stay identical to DocHub.Infrastructure.Audit.ModuleDefinition.Normalize (a test compares them):
// line endings → LF, trailing whitespace removed from every line and the end, the leading CREATE / ALTER / CREATE OR ALTER
// keyword (after leading comments) → CREATE; then SHA-256 over UTF-16LE, upper-case hex.
static string ModuleHash(string definition)
{
    var lines = definition.ReplaceLineEndings("\n").Split('\n').Select(l => l.TrimEnd());
    var text = string.Join('\n', lines).TrimEnd();
    var start = LeadingTriviaLength(text);
    var keyword = Regex.Match(text[start..], @"^(CREATE\s+OR\s+ALTER|ALTER|CREATE)\b", RegexOptions.IgnoreCase);
    if (keyword.Success)
    {
        text = string.Concat(text.AsSpan(0, start), "CREATE", text.AsSpan(start + keyword.Length));
    }

    return Convert.ToHexString(SHA256.HashData(Encoding.Unicode.GetBytes(text)));
}

static int LeadingTriviaLength(string text)
{
    var i = 0;
    while (i < text.Length)
    {
        if (char.IsWhiteSpace(text[i]))
        {
            i++;
        }
        else if (text.AsSpan(i).StartsWith("--"))
        {
            var end = text.IndexOf('\n', i);
            i = end < 0 ? text.Length : end + 1;
        }
        else if (text.AsSpan(i).StartsWith("/*"))
        {
            var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
            i = end < 0 ? text.Length : end + 2;
        }
        else
        {
            break;
        }
    }

    return i;
}

static string GenerateModuleHashes(List<(string Name, string Hash)> modules)
{
    var sb = new StringBuilder();
    sb.AppendLine("// <auto-generated> by database/tools/GenerateAuditTriggers.cs — do not edit by hand. </auto-generated>");
    sb.AppendLine("namespace DocHub.Infrastructure.Audit;");
    sb.AppendLine();
    sb.AppendLine("/// <summary>SHA-256 of the normalized definitions of the audit modules in the DB project (T21 §4, rule 6).</summary>");
    sb.AppendLine("internal static class AuditModuleHashes");
    sb.AppendLine("{");
    sb.AppendLine("    public static IReadOnlyDictionary<string, string> Expected { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)");
    sb.AppendLine("    {");
    foreach (var (name, hash) in modules)
    {
        sb.AppendLine(CultureInfo.InvariantCulture, $"        [\"{name}\"] = \"{hash}\",");
    }

    sb.AppendLine("    };");
    sb.AppendLine("}");
    return sb.ToString();
}

static void Write(string path, string content)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, content.ReplaceLineEndings("\n"), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
}

static string FindRepositoryRoot()
{
    var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DocHub.slnx")))
    {
        directory = directory.Parent;
    }

    return directory?.FullName ?? throw new InvalidOperationException("Run the generator from inside the repository.");
}

internal sealed record AuditedColumn(string Name, bool IsString);

internal sealed record AuditedTable(string Name, string KeyColumn, AuditedColumn[] Columns)
{
    /// <summary>Columns re-rendered by the API: changes to them alone are audited only when they come from a script.</summary>
    public AuditedColumn[] DerivedColumns { get; init; } = [];

    /// <summary>Derived columns whose values are copied into the JSON images.</summary>
    public string[] LoggedDerivedColumns { get; init; } = [];

    /// <summary>SQL condition over [i], [d] and the version [v] that marks a Signed version as tampered with.</summary>
    public string? TamperCondition { get; init; }

    public string? DocumentId { get; init; }

    public string? DocumentVersionId { get; init; }

    public string? LogicalNodeId { get; init; }

    public bool AdvancesVersionStamp { get; init; }

    /// <summary>Changes the version's content hash (nodes and their content): advances VersionStamp.ContentChangeLogId.</summary>
    public bool ChangesContent { get; init; }

    public bool FlagsDerivedStale { get; init; }
}
