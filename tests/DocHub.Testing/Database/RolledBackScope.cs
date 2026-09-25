using Microsoft.Data.SqlClient;

namespace DocHub.Testing.Database;

/// <summary>A connection + transaction that is always rolled back on dispose.</summary>
public sealed class RolledBackScope(SqlConnection connection, SqlTransaction transaction) : ISqlCommands, IAsyncDisposable
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

    /// <summary>Reads all rows as column-name → value dictionaries (DBNull becomes null).</summary>
    public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(string sql, params (string Name, object? Value)[] parameters)
    {
        await using var command = CreateCommand(sql, parameters);
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] = await reader.IsDBNullAsync(i).ConfigureAwait(false) ? null : reader.GetValue(i);
            }

            rows.Add(row);
        }

        return rows;
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
