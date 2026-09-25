using System.Data;
using System.Data.Common;
using DocHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace DocHub.Infrastructure.Procedures;

internal sealed class DbProcedures(DocHubDbContext db) : IDbProcedures
{
    public async Task<PingResult> PingAsync(CancellationToken cancellationToken)
    {
        await using var command = await CreateCommandAsync("app.usp_Ping", cancellationToken).ConfigureAwait(false);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        await ReadSingleRowAsync(reader, "app.usp_Ping", cancellationToken).ConfigureAwait(false);
        int? userId = await reader.IsDBNullAsync(0, cancellationToken).ConfigureAwait(false) ? null : reader.GetInt32(0);
        string? correlationId = await reader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(1);
        var source = reader.GetString(2);

        await NextResultAsync(reader, "app.usp_Ping", cancellationToken).ConfigureAwait(false);
        await ReadSingleRowAsync(reader, "app.usp_Ping", cancellationToken).ConfigureAwait(false);
        var serverTime = reader.GetDateTime(0);

        return new PingResult(userId, correlationId, source, serverTime);
    }

    /// <summary>Creates a stored-procedure command on the context's connection (opened through EF, so the session context is set).</summary>
    private async Task<DbCommand> CreateCommandAsync(string procedure, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var connection = db.Database.GetDbConnection();
        var command = connection.CreateCommand();
        command.CommandText = procedure;
        command.CommandType = CommandType.StoredProcedure;
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        if (db.Database.GetCommandTimeout() is { } timeout)
        {
            command.CommandTimeout = timeout;
        }

        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }

        return command;
    }

    private static async Task ReadSingleRowAsync(DbDataReader reader, string procedure, CancellationToken cancellationToken)
    {
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"{procedure} returned no row.");
        }
    }

    private static async Task NextResultAsync(DbDataReader reader, string procedure, CancellationToken cancellationToken)
    {
        if (!await reader.NextResultAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"{procedure} returned fewer result sets than expected.");
        }
    }
}
