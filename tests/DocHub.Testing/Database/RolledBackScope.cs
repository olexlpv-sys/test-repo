using Microsoft.Data.SqlClient;

namespace DocHub.Testing.Database;

/// <summary>A connection + transaction that is always rolled back on dispose.</summary>
public sealed class RolledBackScope(SqlConnection connection, SqlTransaction transaction) : IAsyncDisposable
{
    public SqlConnection Connection { get; } = connection;

    public SqlTransaction Transaction { get; } = transaction;

    public TestData Data => new(this);

    public async Task<int> ExecuteAsync(string sql, params (string Name, object? Value)[] parameters)
    {
        await using var command = CreateCommand(sql, parameters);
        return await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public async Task<T> ScalarAsync<T>(string sql, params (string Name, object? Value)[] parameters)
    {
        await using var command = CreateCommand(sql, parameters);
        var result = await command.ExecuteScalarAsync().ConfigureAwait(false);
        return (T)Convert.ChangeType(result!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    public async ValueTask DisposeAsync()
    {
        if (Transaction.Connection is not null)
        {
            await Transaction.RollbackAsync().ConfigureAwait(false);
        }

        await Transaction.DisposeAsync().ConfigureAwait(false);
        await Connection.DisposeAsync().ConfigureAwait(false);
    }

    private SqlCommand CreateCommand(string sql, (string Name, object? Value)[] parameters)
    {
#pragma warning disable CA2100 // Test helper: SQL text comes from the tests themselves; values are always parameters.
        var command = new SqlCommand(sql, Connection, Transaction);
#pragma warning restore CA2100
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }
}
