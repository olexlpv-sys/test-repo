namespace DocHub.Testing.Database;

/// <summary>Minimal SQL insert helpers for DB-level tests. Ids of the seeded users/types/folders are stable.</summary>
public sealed class TestData(RolledBackScope scope)
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

    public Task<int> DocumentAsync(string title = "Test document", int folderId = GeneralFolderId, int ownerUserId = AliceUserId) =>
        scope.ScalarAsync<int>(
            "INSERT INTO app.Document (FolderId, Title, OwnerUserId) OUTPUT INSERTED.Id VALUES (@f, @t, @o);",
            ("@f", folderId), ("@t", title), ("@o", ownerUserId));

    public Task<int> VersionAsync(int documentId, byte status = StatusDraft, int? versionNumber = null, bool isCurrent = false) =>
        scope.ScalarAsync<int>(
            """
            INSERT INTO app.DocumentVersion (DocumentId, Status, VersionNumber, SignedAt, CreatedByUserId, IsCurrent)
            OUTPUT INSERTED.Id
            VALUES (@d, @s, @n, CASE WHEN @s = 2 THEN SYSUTCDATETIME() END, @u, @c);
            """,
            ("@d", documentId), ("@s", status), ("@n", versionNumber), ("@u", AliceUserId), ("@c", isCurrent));

    public Task<int> NodeAsync(int versionId, int? parentNodeId = null, string title = "Node", int sortOrder = 1024, Guid? logicalNodeId = null) =>
        scope.ScalarAsync<int>(
            """
            INSERT INTO app.DocumentNode (DocumentVersionId, LogicalNodeId, ParentNodeId, NodeTypeId, Title, SortOrder, CreatedByUserId, ModifiedByUserId)
            OUTPUT INSERTED.Id
            VALUES (@v, @l, @p, @t, @title, @s, @u, @u);
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
