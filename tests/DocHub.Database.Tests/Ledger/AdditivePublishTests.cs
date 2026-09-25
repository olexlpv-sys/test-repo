using DocHub.Testing;
using DocHub.Testing.Database;

namespace DocHub.Database.Tests.Ledger;

/// <summary>
/// T21 §1 change rules: an additive, nullable column on a ledger table is a normal publish (IgnoreColumnOrder) with no drift
/// afterwards, and — with the allow-list updated in the same change — no schema-tampering finding.
/// </summary>
public sealed class AdditivePublishTests(DocHubDatabaseFixture database) : IClassFixture<DocHubDatabaseFixture>
{
    private readonly LedgerHarness _ledger = new(database);

    [Fact]
    public async Task A_nullable_column_added_in_the_project_publishes_without_drift_or_findings()
    {
        var dacpac = NextVersionDacpac("app", "Folder", "Note", "NVARCHAR (100) NULL");

        DacpacDeployer.Deploy(database.MasterConnectionString, database.DatabaseName, dacpac);

        var drift = DacpacDeployer.GetPendingOperations(database.MasterConnectionString, database.DatabaseName, dacpac);
        Assert.True(drift.Count == 0, "Drift after the additive publish:\n" + string.Join('\n', drift));
        await using var dbo = await _ledger.DboAsync();
        Assert.Equal(1, await dbo.ScalarAsync<int>("SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'app.Folder') AND name = N'Note';"));
        Assert.Equal(1, await dbo.ScalarAsync<int>("SELECT COUNT(*) FROM sys.ledger_column_history WHERE object_id = OBJECT_ID(N'app.Folder') AND column_name = N'Note' AND operation_type_desc = N'ADD';"));

        Assert.Equal(0, await _ledger.ReconcileAsync(new DateTime(2000, 1, 1)));
    }

    /// <summary>
    /// The next version of the DACPAC, built like a real change: a copy of the DB project with the column added to the table
    /// script and the allow-list entry the generator emits for it added to the reconciliation procedure.
    /// </summary>
    private static string NextVersionDacpac(string schema, string table, string column, string definition)
    {
        var root = Repository.Root;
        var source = Repository.DatabaseProject;
        var workspace = Directory.CreateTempSubdirectory("DocHub.Database.next.").FullName;
        // Central package versions and build settings come from the repository root.
        foreach (var file in new[] { "Directory.Build.props", "Directory.Packages.props", "global.json" })
        {
            File.Copy(Path.Combine(root, file), Path.Combine(workspace, file));
        }

        var copy = Path.Combine(workspace, "database", "DocHub.Database");
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            if (relative.StartsWith("bin", StringComparison.Ordinal) || relative.StartsWith("obj", StringComparison.Ordinal))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(copy, relative))!);
            File.Copy(file, Path.Combine(copy, relative));
        }

        Edit(Path.Combine(copy, schema, "Tables", $"{table}.sql"), "    [ValidFrom]", $"    [{column}] {definition},\n    [ValidFrom]");
        var allowed = $"(N'{schema}', N'{table}', N'Id'),";
        Edit(Path.Combine(copy, "audit", "StoredProcedures", "usp_ReconcileLedger.sql"), allowed, $"{allowed}\n        (N'{schema}', N'{table}', N'{column}'),");

        using var build = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("dotnet", ["build", Path.Combine(copy, "DocHub.Database.sqlproj"), "-c", "Release", "-nologo", "-v", "q"])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        var output = build.StandardOutput.ReadToEnd() + build.StandardError.ReadToEnd();
        build.WaitForExit();
        Assert.True(build.ExitCode == 0, output);
        return Path.Combine(copy, "bin", "Release", DacpacDeployer.DacpacFileName);
    }

    private static void Edit(string path, string anchor, string replacement)
    {
        var text = File.ReadAllText(path);
        Assert.Contains(anchor, text, StringComparison.Ordinal);
        File.WriteAllText(path, text.Replace(anchor, replacement, StringComparison.Ordinal));
    }
}
