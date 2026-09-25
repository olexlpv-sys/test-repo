// Generates the audit triggers (T03) and the ChangeLog partition function of the DocHub database project.
// Usage (from the repository root):  dotnet run database/tools/GenerateAuditTriggers.cs
// The generated .sql files are committed; CI re-runs this generator and fails if the output differs.
using System.Globalization;
using System.Text;

var root = FindRepositoryRoot();
var project = Path.Combine(root, "database", "DocHub.Database");

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
    },
    new AuditedTable("VersionSignature", "Id", [C("DocumentVersionId"), C("UserId"), C("SignedAt"), C("ContentHash"), C("WithdrawnAt"), S("Comment")])
    {
        DocumentVersionId = "{r}.[DocumentVersionId]",
        AdvancesVersionStamp = true,
    },
    new AuditedTable("DocumentNode", "Id", [C("DocumentVersionId"), C("LogicalNodeId"), C("ParentNodeId"), C("NodeTypeId"), S("Title"), C("SortOrder"), C("CreatedAt"), C("CreatedByUserId"), C("ModifiedAt"), C("ModifiedByUserId")])
    {
        DocumentVersionId = "{r}.[DocumentVersionId]",
        LogicalNodeId = "{r}.[LogicalNodeId]",
        AdvancesVersionStamp = true,
        MarksTampering = true,
    },
    // Derived columns (ContentHtml, PlainText, DerivedStale) are never logged; ContentHash is logged for information but
    // does not make a row "changed" on its own: an update that touches only derived columns is not audited (T03 §2c).
    new AuditedTable("NodeContent", "NodeId", [C("DocumentVersionId"), C("LogicalNodeId"), C("SchemaVersion"), S("ContentJson"), C("ModifiedAt"), C("ModifiedByUserId")])
    {
        InformationalColumns = [C("ContentHash")],
        DocumentVersionId = "{r}.[DocumentVersionId]",
        LogicalNodeId = "{r}.[LogicalNodeId]",
        AdvancesVersionStamp = true,
        MarksTampering = true,
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

Console.WriteLine($"Generated {tables.Length} audit triggers and the ChangeLog partition function.");
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

    var logged = t.Columns.Concat(t.InformationalColumns).ToList();
    var changedAny = string.Join("\n           OR ", t.Columns.Select(c => Distinct(c)));
    var changedList = string.Join(",\n                   ", logged.Select(c => $"CASE WHEN {Distinct(c)} THEN N'{c.Name}' END"));

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
        sb.AppendLine("    -- T03 §2a: a ContentJson change without all derived columns rewritten in the same statement leaves the");
        sb.AppendLine("    -- derived columns stale. Column-based only; session context cannot suppress it.");
        sb.AppendLine("    DECLARE @DerivedRewritten BIT = CASE WHEN UPDATE([ContentHtml]) AND UPDATE([PlainText]) AND UPDATE([ContentHash]) THEN 1 ELSE 0 END;");
        sb.AppendLine();
        sb.AppendLine("    IF UPDATE([ContentJson])");
        sb.AppendLine("    BEGIN");
        sb.AppendLine("        UPDATE [nc]");
        sb.AppendLine("        SET [DerivedStale] = 1");
        sb.AppendLine("        FROM [app].[NodeContent] AS [nc]");
        sb.AppendLine("        JOIN inserted AS [i] ON [i].[NodeId] = [nc].[NodeId]");
        sb.AppendLine("        JOIN deleted AS [d] ON [d].[NodeId] = [i].[NodeId]");
        sb.AppendLine("        WHERE [nc].[DerivedStale] = 0");
        sb.AppendLine("          AND CAST([i].[ContentJson] AS VARBINARY (MAX)) IS DISTINCT FROM CAST([d].[ContentJson] AS VARBINARY (MAX))");
        sb.AppendLine("          AND NOT (@DerivedRewritten = 1 AND [i].[ContentHash] IS DISTINCT FROM [d].[ContentHash]);");
        sb.AppendLine("    END;");
        sb.AppendLine();
    }

    var needsLogTable = t.AdvancesVersionStamp;
    if (needsLogTable)
    {
        sb.AppendLine("    DECLARE @Logged TABLE ([Id] BIGINT NOT NULL, [DocumentVersionId] INT NULL);");
        sb.AppendLine();
    }

    sb.AppendLine("    INSERT INTO [audit].[ChangeLog]");
    sb.AppendLine("        ([TableName], [Operation], [EntityId], [DocumentId], [DocumentVersionId], [LogicalNodeId], [OldValues], [NewValues],");
    sb.AppendLine("         [ChangedColumns], [UserId], [Source], [DbLogin], [AppName], [CorrelationId], [OperationContext], [Ticket], [Reason])");
    if (needsLogTable)
    {
        sb.AppendLine("    OUTPUT INSERTED.[Id], INSERTED.[DocumentVersionId] INTO @Logged ([Id], [DocumentVersionId])");
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
    sb.AppendLine(CultureInfo.InvariantCulture, $"       OR {changedAny};");

    if (t.AdvancesVersionStamp)
    {
        var tamper = t.MarksTampering ? "MAX(CASE WHEN [v].[Status] = 2 THEN SYSUTCDATETIME() END)" : "CAST(NULL AS DATETIME2 (3))";
        sb.AppendLine();
        sb.AppendLine("    -- T03 §2b: advance the per-version stamp (cache key/ETag); flag the first change to a Signed version's nodes/content.");
        sb.AppendLine("    MERGE [app].[VersionStamp] WITH (HOLDLOCK) AS [target]");
        sb.AppendLine("    USING (");
        sb.AppendLine(CultureInfo.InvariantCulture, $"        SELECT [l].[DocumentVersionId], MAX([l].[Id]) AS [LastChangeLogId], {tamper} AS [TamperedAt]");
        sb.AppendLine("        FROM @Logged AS [l]");
        sb.AppendLine("        JOIN [app].[DocumentVersion] AS [v] ON [v].[Id] = [l].[DocumentVersionId]");
        sb.AppendLine("        GROUP BY [l].[DocumentVersionId]) AS [source]");
        sb.AppendLine("    ON [target].[DocumentVersionId] = [source].[DocumentVersionId]");
        sb.AppendLine("    WHEN MATCHED THEN");
        sb.AppendLine("        UPDATE SET [LastChangeLogId] = CASE WHEN [source].[LastChangeLogId] > [target].[LastChangeLogId] THEN [source].[LastChangeLogId] ELSE [target].[LastChangeLogId] END,");
        sb.AppendLine("                   [TamperedAt] = COALESCE([target].[TamperedAt], [source].[TamperedAt])");
        sb.AppendLine("    WHEN NOT MATCHED BY TARGET THEN");
        sb.AppendLine("        INSERT ([DocumentVersionId], [LastChangeLogId], [TamperedAt])");
        sb.AppendLine("        VALUES ([source].[DocumentVersionId], [source].[LastChangeLogId], [source].[TamperedAt]);");
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
    /// <summary>Logged in the JSON images but not considered when deciding whether a row changed.</summary>
    public AuditedColumn[] InformationalColumns { get; init; } = [];

    public string? DocumentId { get; init; }

    public string? DocumentVersionId { get; init; }

    public string? LogicalNodeId { get; init; }

    public bool AdvancesVersionStamp { get; init; }

    public bool MarksTampering { get; init; }

    public bool FlagsDerivedStale { get; init; }
}
