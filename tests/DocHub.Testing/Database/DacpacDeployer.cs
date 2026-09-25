using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Dac;

namespace DocHub.Testing.Database;

/// <summary>Deploys the DocHub DACPAC (the only deployment path, FR-D4) into a database of a SQL Server instance.</summary>
public static partial class DacpacDeployer
{
    public const string DacpacFileName = "DocHub.Database.dacpac";

    /// <summary>First line of Script.PostDeployment.sql.</summary>
    public const string PostDeploymentMarker = "-- <post-deployment>";

    public static string DacpacPath
    {
        get
        {
            var path = Path.Combine(AppContext.BaseDirectory, DacpacFileName);
            return File.Exists(path)
                ? path
                : throw new FileNotFoundException($"'{DacpacFileName}' was not found next to the test assembly. Build the solution first.", path);
        }
    }

    public static void Deploy(string masterConnectionString, string databaseName, string? dacpacPath = null)
    {
        using var package = DacPackage.Load(dacpacPath ?? DacpacPath);
        var services = new DacServices(masterConnectionString);
        services.Deploy(package, databaseName, upgradeExisting: true, CreateOptions());
    }

    /// <summary>
    /// Schema drift: the DDL statements a new deployment would execute — empty when the database matches the DACPAC.
    /// The deploy <em>report</em> can't be used for this: DacFx lists every ledger table as an (empty) "Alter" on each
    /// redeploy, so drift is measured on the generated script (T21 §1).
    /// </summary>
    public static IReadOnlyList<string> GetPendingOperations(string masterConnectionString, string databaseName, string? dacpacPath = null)
    {
        using var package = DacPackage.Load(dacpacPath ?? DacpacPath);
        var services = new DacServices(masterConnectionString);
        var script = services.GenerateDeployScript(package, databaseName, CreateOptions());
        return DriftStatements(script);
    }

    /// <summary>
    /// DDL statements of a deploy script (CREATE/ALTER/DROP/rename) before the post-deployment part (marked
    /// <c>&lt;post-deployment&gt;</c>, which runs on every deployment by design).
    /// </summary>
    public static IReadOnlyList<string> DriftStatements(string deployScript)
    {
        ArgumentNullException.ThrowIfNull(deployScript);
        var postDeployment = deployScript.IndexOf(PostDeploymentMarker, StringComparison.Ordinal);
        var schemaPart = postDeployment < 0 ? deployScript : deployScript[..postDeployment];
        return schemaPart.Split('\n')
            .Select(l => l.Trim())
            .Where(l => DdlStatement().IsMatch(l))
            .ToList();
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^(CREATE|ALTER|DROP)\s|sp_rename", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex DdlStatement();

    /// <summary>
    /// Connection string for tests. Pooling is off: tests impersonate database users (EXECUTE AS) and set session context,
    /// and a pooled session that still carries an impersonation is killed by SQL Server when it is reused.
    /// </summary>
    public static string DatabaseConnectionString(string masterConnectionString, string databaseName) =>
        new SqlConnectionStringBuilder(masterConnectionString) { InitialCatalog = databaseName, Pooling = false }.ConnectionString;

    private static DacDeployOptions CreateOptions() => new()
    {
        BlockOnPossibleDataLoss = true,
        // Ledger tables can't be rebuilt to keep column order; additive columns are appended (T21 §1).
        IgnoreColumnOrder = true,
        DropObjectsNotInSource = true,
        // Roles and permissions are part of the model; users/logins are environment-specific.
        DoNotDropObjectTypes = [ObjectType.Users, ObjectType.Logins, ObjectType.RoleMembership],
        ExcludeObjectTypes = [ObjectType.Users, ObjectType.Logins, ObjectType.RoleMembership],
    };
}
