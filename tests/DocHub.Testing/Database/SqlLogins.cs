using Microsoft.Data.SqlClient;

namespace DocHub.Testing.Database;

/// <summary>
/// Real SQL logins for tests of anything that evaluates the ledger principal (T21): the ledger records the login, and
/// <c>EXECUTE AS USER</c> would still record the original one.
/// </summary>
public static class SqlLogins
{
    /// <summary>
    /// Creates a login and a database user for it (named <paramref name="userName"/>, default: the login name) in the given
    /// roles, and returns a connection string for the login.
    /// </summary>
    public static async Task<string> CreateAsync(DocHubDatabaseFixture database, string? userName = null, params string[] roles)
    {
        ArgumentNullException.ThrowIfNull(database);
        var login = $"test_login_{Guid.NewGuid():N}";
        var password = $"Pw1!{Guid.NewGuid():N}";
        var user = userName ?? login;

        await using (var master = await SqlSession.OpenAsync(database.MasterConnectionString).ConfigureAwait(false))
        {
            await master.ExecuteAsync($"CREATE LOGIN [{login}] WITH PASSWORD = N'{password}', CHECK_POLICY = OFF;").ConfigureAwait(false);
        }

        await using (var db = await SqlSession.OpenAsync(database.ConnectionString).ConfigureAwait(false))
        {
            await db.ExecuteAsync($"CREATE USER [{user}] FOR LOGIN [{login}];").ConfigureAwait(false);
            foreach (var role in roles)
            {
                await db.ExecuteAsync($"ALTER ROLE [{role}] ADD MEMBER [{user}];").ConfigureAwait(false);
            }
        }

        return new SqlConnectionStringBuilder(database.ConnectionString)
        {
            IntegratedSecurity = false,
            UserID = login,
            Password = password,
        }.ConnectionString;
    }
}
