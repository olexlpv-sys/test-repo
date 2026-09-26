using Microsoft.Data.SqlClient;

namespace DocHub.LoadTests;

/// <summary>A generated document as the scenarios use it.</summary>
public sealed record LoadDocument(
    int Id, int FolderId, int OwnerId, int CurrentVersionId, bool HasDraft, int? LatestSignedVersionId, int[] Approvers, int[] Editors,
    (int NodeId, Guid LogicalNodeId)[] Nodes, int NodeCount = 0);

/// <summary>
/// The ids the scenarios pick from: generated documents (not deleted) and users (readers, and each document's roles).
/// <see cref="GeneratedDocuments"/> counts the deleted ones too (the generator's volume).
/// </summary>
public sealed record LoadCatalog(IReadOnlyList<LoadDocument> Documents, IReadOnlyList<int> Readers, int GeneratedDocuments = 0)
{
    /// <summary>The NFR-L5 budgets are defined for documents of this size (the generator's maximum).</summary>
    public const int LargeDocumentNodes = 2000;

    /// <summary>Documents of the budget size (the generator makes some with and some without a draft).</summary>
    public IEnumerable<LoadDocument> Large => Documents.Where(d => d.NodeCount >= LargeDocumentNodes);

    public static async Task<LoadCatalog> ReadAsync(string connectionString, CancellationToken cancellationToken)
    {
        const string Sql = """
            SELECT d.Id, d.FolderId, d.OwnerUserId, v.Id, CAST(CASE WHEN v.Status = 1 THEN 1 ELSE 0 END AS BIT),
                   (SELECT MAX(s.Id) FROM app.DocumentVersion s WHERE s.DocumentId = d.Id AND s.Status = 2),
                   (SELECT COUNT(*) FROM app.DocumentNode n WHERE n.DocumentVersionId = v.Id)
            FROM app.Document d JOIN app.DocumentVersion v ON v.DocumentId = d.Id AND v.IsCurrent = 1
            WHERE d.DeletedAt IS NULL AND EXISTS (SELECT 1 FROM app.[User] u WHERE u.Id = d.OwnerUserId AND u.Login LIKE N'load%');

            SELECT p.DocumentId, p.UserId, p.Role FROM app.DocumentPermission p WHERE p.LogicalNodeId IS NULL;

            SELECT x.DocumentVersionId, x.NodeId, x.LogicalNodeId
            FROM (SELECT c.DocumentVersionId, c.NodeId, c.LogicalNodeId, ROW_NUMBER() OVER (PARTITION BY c.DocumentVersionId ORDER BY c.NodeId) AS r
                  FROM app.NodeContent c JOIN app.DocumentVersion v ON v.Id = c.DocumentVersionId AND v.IsCurrent = 1) AS x
            WHERE x.r <= 20;

            SELECT u.Id FROM app.[User] u WHERE u.Login LIKE N'load%' AND u.DisplayName LIKE N'Reader%';

            SELECT COUNT(*) FROM app.Document d WHERE d.Title LIKE N'Load document%'; -- the generator's documents, deleted ones included
            """;
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(Sql, connection) { CommandTimeout = 0 };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var documents = new List<(int Id, int Folder, int Owner, int Version, bool Draft, int? Signed, int Nodes)>();
        while (await reader.ReadAsync(cancellationToken))
        {
            documents.Add((reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetBoolean(4), reader.IsDBNull(5) ? null : reader.GetInt32(5), reader.GetInt32(6)));
        }

        await reader.NextResultAsync(cancellationToken);
        var grants = new List<(int Document, int User, byte Role)>();
        while (await reader.ReadAsync(cancellationToken))
        {
            grants.Add((reader.GetInt32(0), reader.GetInt32(1), reader.GetByte(2)));
        }

        await reader.NextResultAsync(cancellationToken);
        var nodes = new List<(int Version, int Node, Guid Logical)>();
        while (await reader.ReadAsync(cancellationToken))
        {
            nodes.Add((reader.GetInt32(0), reader.GetInt32(1), reader.GetGuid(2)));
        }

        await reader.NextResultAsync(cancellationToken);
        var readers = new List<int>();
        while (await reader.ReadAsync(cancellationToken))
        {
            readers.Add(reader.GetInt32(0));
        }

        await reader.NextResultAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var generated = reader.GetInt32(0);

        var grantsByDocument = grants.ToLookup(g => g.Document);
        var nodesByVersion = nodes.ToLookup(n => n.Version);
        return new LoadCatalog(
            documents.Where(d => nodesByVersion[d.Version].Any())
                .Select(d => new LoadDocument(d.Id, d.Folder, d.Owner, d.Version, d.Draft, d.Signed,
                    [.. grantsByDocument[d.Id].Where(g => g.Role == 2).Select(g => g.User)],
                    [.. grantsByDocument[d.Id].Where(g => g.Role == 1).Select(g => g.User)],
                    [.. nodesByVersion[d.Version].Select(n => (n.Node, n.Logical))], d.Nodes))
                .ToList(),
            readers,
            generated);
    }
}
