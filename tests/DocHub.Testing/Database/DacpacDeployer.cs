using System.Xml.Linq;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Dac;

namespace DocHub.Testing.Database;

/// <summary>Deploys the DocHub DACPAC (the only deployment path, FR-D4) into a database of a SQL Server instance.</summary>
public static class DacpacDeployer
{
    public const string DacpacFileName = "DocHub.Database.dacpac";

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

    public static void Deploy(string masterConnectionString, string databaseName)
    {
        using var package = DacPackage.Load(DacpacPath);
        var services = new DacServices(masterConnectionString);
        services.Deploy(package, databaseName, upgradeExisting: true, CreateOptions());
    }

    /// <summary>Returns the operations a new deployment would perform — empty when the database matches the DACPAC (no drift).</summary>
    public static IReadOnlyList<string> GetPendingOperations(string masterConnectionString, string databaseName)
    {
        using var package = DacPackage.Load(DacpacPath);
        var services = new DacServices(masterConnectionString);
        var report = XDocument.Parse(services.GenerateDeployReport(package, databaseName, CreateOptions()));

        return report.Descendants()
            .Where(e => e.Name.LocalName == "Item" && e.Parent?.Name.LocalName == "Operation")
            .Select(e => $"{e.Parent!.Attribute("Name")?.Value}: {e.Attribute("Value")?.Value}")
            .ToList();
    }

    /// <summary>
    /// Connection string for tests. Pooling is off: tests impersonate database users (EXECUTE AS) and set session context,
    /// and a pooled session that still carries an impersonation is killed by SQL Server when it is reused.
    /// </summary>
    public static string DatabaseConnectionString(string masterConnectionString, string databaseName) =>
        new SqlConnectionStringBuilder(masterConnectionString) { InitialCatalog = databaseName, Pooling = false }.ConnectionString;

    private static DacDeployOptions CreateOptions() => new()
    {
        BlockOnPossibleDataLoss = true,
        DropObjectsNotInSource = true,
        // Roles and permissions are part of the model; users/logins are environment-specific.
        DoNotDropObjectTypes = [ObjectType.Users, ObjectType.Logins, ObjectType.RoleMembership],
        ExcludeObjectTypes = [ObjectType.Users, ObjectType.Logins, ObjectType.RoleMembership],
    };
}
