namespace DocHub.Infrastructure.Procedures;

/// <summary>
/// Typed access to the database's stored procedures (ADR-09): one method per procedure, always parameterized, executed on
/// the DbContext connection so the session context (audit) applies. Plain CRUD never goes through here (FR-D1).
/// </summary>
public interface IDbProcedures
{
    /// <summary><c>app.usp_Ping</c> — establishes the pattern: a row from the session context plus a second result set.</summary>
    Task<PingResult> PingAsync(CancellationToken cancellationToken);

    /// <summary><c>app.usp_CheckPermission</c> — one action on a document for a user (FR-D2).</summary>
    Task<bool> CheckPermissionAsync(int documentId, int userId, PermissionAction action, int? documentVersionId, Guid? logicalNodeId, CancellationToken cancellationToken);

    /// <summary><c>app.usp_ListDocuments</c> — one page of a folder's documents plus the total count.</summary>
    Task<DocumentListPage> ListDocumentsAsync(DocumentListQuery query, CancellationToken cancellationToken);

    /// <summary><c>app.usp_CopyVersionToDraft</c> — deep copy of a version into a new draft; returns the new version id.</summary>
    Task<int> CopyVersionToDraftAsync(int sourceVersionId, int userId, CancellationToken cancellationToken);
}

/// <summary>Actions of <c>app.usp_CheckPermission</c>.</summary>
public enum PermissionAction
{
    View,
    Manage,
    EditStructure,
    EditContent,
    Comment,
    Resolve,
    Sign,
    Move,
    Restore,
}

public sealed record DocumentListQuery(
    int FolderId, int UserId, bool IncludeDeleted, bool IncludeSubfolders, string? Search, string? Status, string SortBy, string SortDir, int Page, int PageSize);

public sealed record DocumentListRow(
    int Id, byte[] RowVersion, int FolderId, string Title, string Status, int? LatestSignedVersion, int? DraftVersionId, bool? DraftHashValid,
    int? SignaturesSigned, int? SignaturesRequired, int OwnerUserId, string OwnerDisplayName, bool IsOwner, bool IsEditor, bool IsApprover,
    DateTime CreatedAt, DateTime ModifiedAt);

public sealed record DocumentListPage(IReadOnlyList<DocumentListRow> Items, int TotalCount);

public sealed record PingResult(int? UserId, string? CorrelationId, string Source, DateTime ServerTimeUtc);
