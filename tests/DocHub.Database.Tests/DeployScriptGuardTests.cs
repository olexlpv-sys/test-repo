using System.Text.RegularExpressions;
using DocHub.Testing;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace DocHub.Database.Tests;

/// <summary>
/// T21 §3: pre/post-deployment scripts run with the pipeline's privileges, so the repository rejects scripts that could forge
/// data or evidence: DML outside the seed tables, anything touching <c>audit.*</c>/<c>history.*</c>, disabling triggers or
/// constraints, versioning/ledger options, session context and dynamic SQL.
/// </summary>
public sealed partial class DeployScriptGuardTests
{
    private static readonly string[] ScriptDirectories = ["PreDeployment", "PostDeployment"];

    public static TheoryData<string> DeployScripts()
    {
        var data = new TheoryData<string>();
        foreach (var directory in ScriptDirectories.Select(d => Path.Combine(Repository.DatabaseProject, "Scripts", d)).Where(Directory.Exists))
        {
            foreach (var file in Directory.GetFiles(directory, "*.sql", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                data.Add(Path.GetRelativePath(Repository.DatabaseProject, file));
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(DeployScripts))]
    public void Deploy_script_passes_the_guard(string script)
    {
        var path = Path.Combine(Repository.DatabaseProject, script);

        Assert.Empty(DeployScriptGuard.Check(File.ReadAllText(path), Path.GetDirectoryName(path)!));
    }

    [Fact]
    public void The_real_deploy_scripts_are_checked() => Assert.NotEmpty(DeployScripts());

    [Theory]
    [InlineData("UPDATE app.NodeContent SET ContentJson = N'{}';", "DML on app.NodeContent")]
    [InlineData("DELETE FROM app.Document;", "DML on app.Document")]
    [InlineData("INSERT INTO app.DocumentPermission (DocumentId, UserId, Role) VALUES (1, 2, 1);", "DML on app.DocumentPermission")]
    [InlineData("UPDATE [x] SET Title = N'' FROM [app].[DocumentNode] AS [x];", "DML on app.DocumentNode")]
    [InlineData("MERGE app.Comment AS t USING (SELECT 1 AS Id) AS s ON t.Id = s.Id WHEN MATCHED THEN DELETE;", "DML on app.Comment")]
    [InlineData("TRUNCATE TABLE app.VersionSignature;", "DML on app.VersionSignature")]
    [InlineData("DECLARE @t TABLE (Id INT); INSERT INTO app.[User] (Login, DisplayName) OUTPUT INSERTED.Id INTO app.Document (Id) VALUES (N'x', N'x');", "DML on app.Document")]
    [InlineData("INSERT INTO audit.ReconciliationBaseline (CreatedAt, Reason) VALUES (SYSUTCDATETIME(), N'x');", "touches audit.ReconciliationBaseline")]
    [InlineData("SELECT * FROM audit.ChangeLog;", "touches audit.ChangeLog")]
    [InlineData("SELECT * FROM history.NodeContent;", "touches history.NodeContent")]
    [InlineData("DISABLE TRIGGER app.TR_NodeContent_Audit ON app.NodeContent;", "DISABLE TRIGGER")]
    [InlineData("ALTER TABLE app.NodeContent DISABLE TRIGGER ALL;", "DISABLE TRIGGER")]
    [InlineData("ALTER TABLE app.NodeContent NOCHECK CONSTRAINT ALL;", "NOCHECK")]
    [InlineData("ALTER TABLE app.NodeContent SET (SYSTEM_VERSIONING = OFF);", "SYSTEM_VERSIONING")]
    [InlineData("CREATE TABLE dbo.Shadow (Id INT NOT NULL) WITH (LEDGER = ON);", "LEDGER")]
    [InlineData("EXEC sys.sp_set_session_context @key = N'UserId', @value = 1;", "sp_set_session_context")]
    [InlineData("DECLARE @s NVARCHAR (100) = N'SELECT 1'; EXEC (@s);", "dynamic SQL")]
    [InlineData("EXEC sys.sp_executesql N'SELECT 1';", "dynamic SQL")]
    [InlineData("EXECUTE sp_executesql N'SELECT 1';", "dynamic SQL")]
    [InlineData(":r ..\\..\\elsewhere.sql", ":r outside the deploy script directory")]
    [InlineData("UPDATE app.Folder SET Name = ", "does not parse")]
    public void Forbidden_statements_fail_the_guard(string sql, string expected)
    {
        var violations = DeployScriptGuard.Check(sql, Path.GetTempPath());

        Assert.Contains(violations, v => v.Contains(expected, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("INSERT INTO [app].[Folder] ([Name], [SortOrder], [CreatedByUserId]) VALUES (N'x', 1, 1);")]
    [InlineData("UPDATE [c] SET [Name] = N'x' FROM [app].[ContentStyle] AS [c];")]
    [InlineData("MERGE [app].[User] AS [target] USING (SELECT 1 AS [Id]) AS [source] ON [target].[Id] = [source].[Id] WHEN MATCHED THEN UPDATE SET [Login] = N'x';")]
    [InlineData("DECLARE @t TABLE (Id INT); INSERT INTO @t VALUES (1); UPDATE [x] SET [Id] = 2 FROM @t AS [x]; CREATE TABLE #s (Id INT); INSERT INTO #s VALUES (1);")]
    [InlineData("IF IS_ROLEMEMBER(N'ledger_reader', N'app_api') = 0 ALTER ROLE [ledger_reader] ADD MEMBER [app_api];")]
    public void Seed_statements_pass_the_guard(string sql) => Assert.Empty(DeployScriptGuard.Check(sql, Path.GetTempPath()));

    /// <summary>The guard itself: a ScriptDom walk over the script (SQLCMD <c>:r</c> lines are checked, not parsed).</summary>
    private static partial class DeployScriptGuard
    {
        private static readonly HashSet<string> SeedTables = new(StringComparer.OrdinalIgnoreCase) { "app.User", "app.NodeType", "app.ContentStyle", "app.Folder" };

        public static List<string> Check(string script, string directory)
        {
            var violations = new List<string>();
            var lines = script.ReplaceLineEndings("\n").Split('\n');
            foreach (var include in lines.Select(l => IncludeLine().Match(l)).Where(m => m.Success))
            {
                var target = Path.GetFullPath(Path.Combine(directory, include.Groups[1].Value.Trim().Replace('\\', Path.DirectorySeparatorChar)));
                if (!string.Equals(Path.GetDirectoryName(target), Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal))
                {
                    violations.Add($":r outside the deploy script directory: {include.Value.Trim()}");
                }
            }

            var sql = string.Join('\n', lines.Select(l => l.TrimStart().StartsWith(':') ? "" : l));
            var fragment = new TSql160Parser(initialQuotedIdentifiers: true).Parse(new StringReader(sql), out var errors);
            if (errors.Count > 0)
            {
                violations.Add("does not parse: " + string.Join("; ", errors.Select(e => $"{e.Line}:{e.Column} {e.Message}")));
                return violations;
            }

            var visitor = new Visitor(violations);
            fragment.Accept(visitor);
            return violations;
        }

        [GeneratedRegex(@"^\s*:r\s+(.+)$", RegexOptions.IgnoreCase)]
        private static partial Regex IncludeLine();

        private static string Name(SchemaObjectName name) =>
            name.SchemaIdentifier is null ? name.BaseIdentifier.Value : $"{name.SchemaIdentifier.Value}.{name.BaseIdentifier.Value}";

        private sealed class Visitor(List<string> violations) : TSqlFragmentVisitor
        {
            public override void Visit(SchemaObjectName node)
            {
                var schema = node.SchemaIdentifier?.Value;
                if (string.Equals(schema, "audit", StringComparison.OrdinalIgnoreCase) || string.Equals(schema, "history", StringComparison.OrdinalIgnoreCase))
                {
                    violations.Add($"touches {Name(node)}");
                }
            }

            public override void Visit(InsertSpecification node) => CheckTarget(node.Target, null);

            public override void Visit(UpdateSpecification node) => CheckTarget(node.Target, node.FromClause);

            public override void Visit(DeleteSpecification node) => CheckTarget(node.Target, node.FromClause);

            public override void Visit(MergeSpecification node) => CheckTarget(node.Target, null);

            public override void Visit(OutputIntoClause node) => CheckTarget(node.IntoTable, null);

            public override void Visit(TruncateTableStatement node) => CheckTable(Name(node.TableName));

            public override void Visit(EnableDisableTriggerStatement node)
            {
                if (node.TriggerEnforcement == TriggerEnforcement.Disable)
                {
                    violations.Add("DISABLE TRIGGER");
                }
            }

            public override void Visit(AlterTableTriggerModificationStatement node)
            {
                if (node.TriggerEnforcement == TriggerEnforcement.Disable)
                {
                    violations.Add("DISABLE TRIGGER");
                }
            }

            public override void Visit(AlterTableConstraintModificationStatement node)
            {
                if (node.ConstraintEnforcement == ConstraintEnforcement.NoCheck)
                {
                    violations.Add("NOCHECK");
                }
            }

            public override void Visit(SystemVersioningTableOption node) => violations.Add("SYSTEM_VERSIONING option");

            public override void Visit(LedgerTableOption node) => violations.Add("LEDGER option");

            public override void Visit(ExecuteStatement node)
            {
                switch (node.ExecuteSpecification?.ExecutableEntity)
                {
                    case ExecutableStringList:
                        violations.Add("dynamic SQL: EXEC (…)");
                        break;
                    case ExecutableProcedureReference { ProcedureReference.ProcedureReference.Name: { } name }:
                        var procedure = name.BaseIdentifier.Value;
                        if (string.Equals(procedure, "sp_executesql", StringComparison.OrdinalIgnoreCase))
                        {
                            violations.Add("dynamic SQL: sp_executesql");
                        }
                        else if (string.Equals(procedure, "sp_set_session_context", StringComparison.OrdinalIgnoreCase))
                        {
                            violations.Add("sp_set_session_context");
                        }

                        break;
                }
            }

            private void CheckTarget(TableReference? target, FromClause? from)
            {
                switch (target)
                {
                    case NamedTableReference named when named.SchemaObject.SchemaIdentifier is null:
                        // An alias (UPDATE [c] … FROM [app].[X] AS [c]) or a temp table.
                        var alias = named.SchemaObject.BaseIdentifier.Value;
                        var aliased = (from?.TableReferences ?? []).SelectMany(Flatten)
                            .OfType<TableReferenceWithAlias>()
                            .FirstOrDefault(t => string.Equals(t.Alias?.Value, alias, StringComparison.OrdinalIgnoreCase));
                        if (aliased is NamedTableReference aliasedTable)
                        {
                            CheckTable(Name(aliasedTable.SchemaObject));
                        }
                        else if (aliased is null && !alias.StartsWith('#'))
                        {
                            violations.Add($"DML on {alias} (unqualified)");
                        }

                        break;
                    case NamedTableReference named:
                        CheckTable(Name(named.SchemaObject));
                        break;
                    case VariableTableReference:
                    case null:
                        break;
                    default:
                        violations.Add($"DML on {target.GetType().Name}");
                        break;
                }
            }

            private static IEnumerable<TableReference> Flatten(TableReference reference) => reference switch
            {
                JoinTableReference join => Flatten(join.FirstTableReference).Concat(Flatten(join.SecondTableReference)),
                JoinParenthesisTableReference parenthesis => Flatten(parenthesis.Join),
                _ => [reference],
            };

            private void CheckTable(string table)
            {
                if (!SeedTables.Contains(table))
                {
                    violations.Add($"DML on {table}");
                }
            }
        }
    }
}
