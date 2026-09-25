using DocHub.Domain.Errors;

namespace DocHub.Api.Errors;

/// <summary>Maps unique indexes/constraints of the database project to stable error codes (architecture §3).</summary>
internal static class UniqueConstraintCodes
{
    private static readonly (string Prefix, string Code)[] Map =
    [
        ("UX_Folder_", ErrorCodes.DuplicateName),
        ("UQ_NodeType_Code", ErrorCodes.DuplicateName),
        ("UQ_ContentStyle_StyleId", ErrorCodes.DuplicateName),
        ("UQ_User_Login", ErrorCodes.DuplicateName),
        ("UX_DocumentPermission_", ErrorCodes.DuplicateGrant),
        ("UX_DocumentVersion_OneDraft", "draft-already-exists"),
    ];

    public static string For(string? indexName) =>
        indexName is null
            ? ErrorCodes.Conflict
            : Map.FirstOrDefault(m => indexName.StartsWith(m.Prefix, StringComparison.Ordinal)).Code ?? ErrorCodes.Conflict;
}
