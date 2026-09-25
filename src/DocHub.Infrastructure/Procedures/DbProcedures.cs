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

    public async Task<bool> CheckPermissionAsync(int documentId, int userId, PermissionAction action, int? documentVersionId, Guid? logicalNodeId, CancellationToken cancellationToken)
    {
        await using var command = await CreateCommandAsync("app.usp_CheckPermission", cancellationToken,
            ("@DocumentId", documentId), ("@UserId", userId), ("@Action", action.ToString()), ("@DocumentVersionId", documentVersionId), ("@LogicalNodeId", logicalNodeId)).ConfigureAwait(false);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await ReadSingleRowAsync(reader, "app.usp_CheckPermission", cancellationToken).ConfigureAwait(false);
        return reader.GetBoolean(0);
    }

    public async Task<EffectivePermissions> GetEffectivePermissionsAsync(int documentId, int userId, int? documentVersionId, CancellationToken cancellationToken)
    {
        await using var command = await CreateCommandAsync("app.usp_GetEffectivePermissions", cancellationToken,
            ("@DocumentId", documentId), ("@UserId", userId), ("@DocumentVersionId", documentVersionId)).ConfigureAwait(false);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await ReadSingleRowAsync(reader, "app.usp_GetEffectivePermissions", cancellationToken).ConfigureAwait(false);
        bool Flag(string name) => reader.GetBoolean(reader.GetOrdinal(name));
        var (isOwner, isAdmin, isEditor, isApprover) = (Flag("IsOwner"), Flag("IsAdmin"), Flag("IsEditor"), Flag("IsApprover"));
        var (canView, canEditStructure, canEditAllContent, canComment, canResolve) = (Flag("CanView"), Flag("CanEditStructure"), Flag("CanEditAllContent"), Flag("CanComment"), Flag("CanResolve"));
        var (canSign, canManage, canMove, canRestore) = (Flag("CanSign"), Flag("CanManage"), Flag("CanMove"), Flag("CanRestore"));
        var versionId = NullableInt(reader, reader.GetOrdinal("DocumentVersionId"));
        await NextResultAsync(reader, "app.usp_GetEffectivePermissions", cancellationToken).ConfigureAwait(false);
        var nodes = new List<Guid>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            nodes.Add(reader.GetGuid(0));
        }

        return new EffectivePermissions(isOwner, isAdmin, isEditor, isApprover, canView, canEditStructure, canEditAllContent, canComment, canResolve,
            canSign, canManage, canMove, canRestore, versionId, nodes);
    }

    public async Task<DocumentListPage> ListDocumentsAsync(DocumentListQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using var command = await CreateCommandAsync("app.usp_ListDocuments", cancellationToken,
            ("@FolderId", query.FolderId), ("@UserId", query.UserId), ("@IncludeDeleted", query.IncludeDeleted), ("@IncludeSubfolders", query.IncludeSubfolders),
            ("@Search", query.Search), ("@Status", query.Status), ("@SortBy", query.SortBy), ("@SortDir", query.SortDir), ("@Page", query.Page), ("@PageSize", query.PageSize)).ConfigureAwait(false);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var rows = new List<DocumentListRow>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new DocumentListRow(
                reader.GetInt32(0), (byte[])reader[1], reader.GetInt32(2), reader.GetString(3), reader.GetString(4),
                NullableInt(reader, 5), NullableInt(reader, 6), reader.IsDBNull(7) ? null : reader.GetBoolean(7),
                NullableInt(reader, 8), NullableInt(reader, 9), reader.GetInt32(10), reader.GetString(11),
                reader.GetBoolean(12), reader.GetBoolean(13), reader.GetBoolean(14), reader.GetDateTime(15), reader.GetDateTime(16)));
        }

        await NextResultAsync(reader, "app.usp_ListDocuments", cancellationToken).ConfigureAwait(false);
        await ReadSingleRowAsync(reader, "app.usp_ListDocuments", cancellationToken).ConfigureAwait(false);
        return new DocumentListPage(rows, reader.GetInt32(0));
    }

    public async Task<int> CopyVersionToDraftAsync(int sourceVersionId, int userId, CancellationToken cancellationToken)
    {
        await using var command = await CreateCommandAsync("app.usp_CopyVersionToDraft", cancellationToken, ("@SourceVersionId", sourceVersionId), ("@UserId", userId)).ConfigureAwait(false);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await ReadSingleRowAsync(reader, "app.usp_CopyVersionToDraft", cancellationToken).ConfigureAwait(false);
        return reader.GetInt32(0);
    }

    public async Task<int> DeleteSubtreeAsync(int nodeId, CancellationToken cancellationToken)
    {
        await using var command = await CreateCommandAsync("app.usp_DeleteSubtree", cancellationToken, ("@NodeId", nodeId)).ConfigureAwait(false);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await ReadSingleRowAsync(reader, "app.usp_DeleteSubtree", cancellationToken).ConfigureAwait(false);
        return reader.GetInt32(0);
    }

    private static int? NullableInt(DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

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
