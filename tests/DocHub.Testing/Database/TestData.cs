namespace DocHub.Testing.Database;

/// <summary>
/// Minimal SQL insert helpers for DB-level tests. Ids of the seeded users/types/folders are stable.
/// Audited tables have triggers, so inserts return ids via SCOPE_IDENTITY() (OUTPUT without INTO is not allowed).
/// </summary>
public sealed class TestData(ISqlCommands scope)
{
    public const int AdminUserId = 1;
    public const int AliceUserId = 2;
    public const int BobUserId = 3;
    public const int CarolUserId = 4;
    public const int ChapterNodeTypeId = 1;
    public const int SectionNodeTypeId = 2;
    public const int GeneralFolderId = 1;

    public const byte StatusDraft = 1;
    public const byte StatusSigned = 2;
    public const byte StatusDeleted = 3;

    private static readonly byte[] EmptyHash = new byte[32];

    /// <summary>Creates a database user without login in <paramref name="role"/> (rolled back with a <see cref="RolledBackScope"/>) and returns its name.</summary>
    public async Task<string> DatabaseUserInRoleAsync(string role)
    {
        var name = $"test_{role}_{Guid.NewGuid():N}";
        await scope.ExecuteAsync($"CREATE USER [{name}] WITHOUT LOGIN; ALTER ROLE [{role}] ADD MEMBER [{name}];").ConfigureAwait(false);
        return name;
    }

    public Task<int> DocumentAsync(string title = "Test document", int folderId = GeneralFolderId, int ownerUserId = AliceUserId) =>
        scope.ScalarAsync<int>(
            "INSERT INTO app.Document (FolderId, Title, OwnerUserId) VALUES (@f, @t, @o); SELECT CAST(SCOPE_IDENTITY() AS int);",
            ("@f", folderId), ("@t", title), ("@o", ownerUserId));

    public Task<int> VersionAsync(int documentId, byte status = StatusDraft, int? versionNumber = null, bool isCurrent = false) =>
        scope.ScalarAsync<int>(
            """
            INSERT INTO app.DocumentVersion (DocumentId, Status, VersionNumber, SignedAt, CreatedByUserId, IsCurrent)
            VALUES (@d, @s, @n, CASE WHEN @s = 2 THEN SYSUTCDATETIME() END, @u, @c);
            SELECT CAST(SCOPE_IDENTITY() AS int);
            """,
            ("@d", documentId), ("@s", status), ("@n", versionNumber), ("@u", AliceUserId), ("@c", isCurrent));

    public Task<int> NodeAsync(int versionId, int? parentNodeId = null, string title = "Node", int sortOrder = 1024, Guid? logicalNodeId = null) =>
        scope.ScalarAsync<int>(
            """
            INSERT INTO app.DocumentNode (DocumentVersionId, LogicalNodeId, ParentNodeId, NodeTypeId, Title, SortOrder, CreatedByUserId, ModifiedByUserId)
            VALUES (@v, @l, @p, @t, @title, @s, @u, @u);
            SELECT CAST(SCOPE_IDENTITY() AS int);
            """,
            ("@v", versionId), ("@l", logicalNodeId ?? Guid.NewGuid()), ("@p", parentNodeId), ("@t", ChapterNodeTypeId),
            ("@title", title), ("@s", sortOrder), ("@u", AliceUserId));

    public Task ContentAsync(int nodeId, string contentJson = """{"type":"doc","content":[]}""") =>
        scope.ExecuteAsync(
            """
            INSERT INTO app.NodeContent (NodeId, DocumentVersionId, LogicalNodeId, ContentJson, ContentHash, ModifiedByUserId)
            SELECT n.Id, n.DocumentVersionId, n.LogicalNodeId, @j, @h, @u FROM app.DocumentNode AS n WHERE n.Id = @n;
            """,
            ("@n", nodeId), ("@j", contentJson), ("@h", EmptyHash), ("@u", AliceUserId));
}
