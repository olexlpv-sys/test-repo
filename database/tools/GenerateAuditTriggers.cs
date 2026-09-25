// Generates the audit triggers (T03) and the ChangeLog partition function of the DocHub database project.
// Usage (from the repository root):  dotnet run database/tools/GenerateAuditTriggers.cs
// The generated .sql files are committed; CI re-runs this generator and fails if the output differs.
using System.Globalization;
using System.Text;

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
        // A Signed version whose signing data changes (unsign, re-sign, new hash/number/time) is tampering; IsCurrent flips legitimately.
        TamperCondition = "[d].[Status] = 2 AND ([i].[Id] IS NULL OR [i].[Status] IS DISTINCT FROM [d].[Status] OR [i].[VersionNumber] IS DISTINCT FROM [d].[VersionNumber] OR [i].[SignedAt] IS DISTINCT FROM [d].[SignedAt] OR [i].[SignedContentHash] IS DISTINCT FROM [d].[SignedContentHash] OR [i].[DocumentId] IS DISTINCT FROM [d].[DocumentId])",
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
        sb.AppendLine("    IF UPDATE([ContentJson]) OR UPDATE([ContentHtml]) OR UPDATE([PlainText]) OR UPDATE([ContentHash]) OR UPDATE([DerivedStale])");
        sb.AppendLine("    BEGIN");
        sb.AppendLine("        UPDATE [nc]");
        sb.AppendLine("        SET [DerivedStale] = 1");
        sb.AppendLine("        FROM [app].[NodeContent] AS [nc]");
        sb.AppendLine("        JOIN inserted AS [i] ON [i].[NodeId] = [nc].[NodeId]");
        sb.AppendLine("        JOIN deleted AS [d] ON [d].[NodeId] = [i].[NodeId]");
        sb.AppendLine("        WHERE [nc].[DerivedStale] = 0");
        sb.AppendLine("          AND (");
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
        sb.AppendLine("    DECLARE @Logged TABLE ([Id] BIGINT NOT NULL, [EntityId] INT NOT NULL);");
        sb.AppendLine();
    }

    sb.AppendLine("    INSERT INTO [audit].[ChangeLog]");
    sb.AppendLine("        ([TableName], [Operation], [EntityId], [DocumentId], [DocumentVersionId], [LogicalNodeId], [OldValues], [NewValues],");
    sb.AppendLine("         [ChangedColumns], [UserId], [Source], [DbLogin], [AppName], [CorrelationId], [OperationContext], [Ticket], [Reason])");
    if (needsLogTable)
    {
        sb.AppendLine("    OUTPUT INSERTED.[Id], INSERTED.[EntityId] INTO @Logged ([Id], [EntityId])");
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
        var tamper = t.TamperCondition is null ? "CAST(NULL AS DATETIME2 (7))" : $"MAX(CASE WHEN {t.TamperCondition} THEN SYSUTCDATETIME() END)";
        var versionOf = (string alias) => t.DocumentVersionId!.Replace("{r}", alias, StringComparison.Ordinal);
        sb.AppendLine();
        sb.AppendLine("    -- T03 §2b: advance the per-version stamp (cache key/ETag) of every version the change touches — old and new version");
        sb.AppendLine("    -- for rows that move between versions — and record the first tampering with a Signed version.");
        sb.AppendLine("    MERGE [app].[VersionStamp] WITH (HOLDLOCK) AS [target]");
        sb.AppendLine("    USING (");
        sb.AppendLine(CultureInfo.InvariantCulture, $"        SELECT [x].[DocumentVersionId], MAX([l].[Id]) AS [LastChangeLogId], {tamper} AS [TamperedAt]");
        sb.AppendLine("        FROM @Logged AS [l]");
        sb.AppendLine(CultureInfo.InvariantCulture, $"        LEFT JOIN inserted AS [i] ON [i].[{key}] = [l].[EntityId]");
        sb.AppendLine(CultureInfo.InvariantCulture, $"        LEFT JOIN deleted AS [d] ON [d].[{key}] = [l].[EntityId]");
        sb.AppendLine(CultureInfo.InvariantCulture, $"        CROSS APPLY (VALUES ({versionOf("[i]")}), ({versionOf("[d]")})) AS [x] ([DocumentVersionId])");
        sb.AppendLine("        JOIN [app].[DocumentVersion] AS [v] ON [v].[Id] = [x].[DocumentVersionId]");
        sb.AppendLine("        GROUP BY [x].[DocumentVersionId]) AS [source]");
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

    public bool FlagsDerivedStale { get; init; }
}
