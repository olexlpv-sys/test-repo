using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace DocHub.Infrastructure.Persistence;

/// <summary>
/// Sets <c>SESSION_CONTEXT</c> keys (<c>UserId</c>, <c>CorrelationId</c>, <c>OperationContext</c>) whenever EF Core opens a
/// connection. Pooled connections are reset by <c>sp_reset_connection</c>, which clears the session context.
/// </summary>
public sealed class SessionContextConnectionInterceptor(IDbSessionContext context) : DbConnectionInterceptor
{
    private const string Sql =
        "EXEC sys.sp_set_session_context @key = N'UserId', @value = @UserId; " +
        "EXEC sys.sp_set_session_context @key = N'CorrelationId', @value = @CorrelationId; " +
        "EXEC sys.sp_set_session_context @key = N'OperationContext', @value = @OperationContext;";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using var command = CreateCommand(connection);
        command.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var command = CreateCommand(connection);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private DbCommand CreateCommand(DbConnection connection)
    {
        var command = connection.CreateCommand();
        command.CommandText = Sql;
        command.CommandType = CommandType.Text;
        AddParameter(command, "@UserId", context.UserId);
        AddParameter(command, "@CorrelationId", context.CorrelationId);
        AddParameter(command, "@OperationContext", context.OperationContext);
        return command;
    }

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
