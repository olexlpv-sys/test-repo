using Microsoft.Data.SqlClient;

namespace DocHub.Api.Tests.Infrastructure;

/// <summary>Direct SQL for arranging data and asserting on the database (as the container administrator).</summary>
internal static class Db
{
    public static async Task<IReadOnlyList<Dictionary<string, object?>>> QueryAsync(DocHubApiFactory factory, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var connection = new SqlConnection(factory.AdminConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
#pragma warning disable CA2100 // SQL text comes from the tests; values are parameters.
        await using var command = new SqlCommand(sql, connection);
#pragma warning restore CA2100
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var rows = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }

            rows.Add(row);
        }

        return rows;
    }
}
