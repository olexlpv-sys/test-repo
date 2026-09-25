/*
Effective rights of a user on a document (FR-D2, T10) — the same rules as usp_CheckPermission, all at once:
  result set 1 (one row): IsOwner, IsAdmin, IsEditor, IsApprover, CanView, CanEditStructure, CanEditAllContent, CanComment,
                          CanResolve, CanSign, CanManage, CanMove, CanRestore, DocumentVersionId (the version the node set is for)
  result set 2: EditableLogicalNodeIds — the user's node-level editor grants expanded to their descendants in the tree of
                @DocumentVersionId (default: the draft, else the current version). Empty when the user may edit all content
                or nothing.
An unknown document returns all zeros and no nodes. A deleted document allows View (owner/admin), Move (admin) and Restore only.
*/
CREATE PROCEDURE [app].[usp_GetEffectivePermissions]
    @DocumentId        INT,
    @UserId            INT,
    @DocumentVersionId INT = NULL
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @OwnerUserId INT, @Deleted BIT;
    SELECT @OwnerUserId = [OwnerUserId], @Deleted = CASE WHEN [DeletedAt] IS NULL THEN 0 ELSE 1 END
    FROM [app].[Document]
    WHERE [Id] = @DocumentId;

    DECLARE @Exists BIT = CASE WHEN @OwnerUserId IS NULL THEN 0 ELSE 1 END;
    DECLARE @IsAdmin BIT = COALESCE((SELECT [IsAdmin] FROM [app].[User] WHERE [Id] = @UserId AND [IsActive] = 1), 0);
    DECLARE @IsOwner BIT = CASE WHEN @OwnerUserId = @UserId THEN 1 ELSE 0 END;
    DECLARE @IsApprover BIT = CASE WHEN EXISTS (SELECT 1 FROM [app].[DocumentPermission]
                                                WHERE [DocumentId] = @DocumentId AND [UserId] = @UserId AND [Role] = 2) THEN 1 ELSE 0 END;
    DECLARE @IsEditor BIT = CASE WHEN EXISTS (SELECT 1 FROM [app].[DocumentPermission]
                                              WHERE [DocumentId] = @DocumentId AND [UserId] = @UserId AND [Role] = 1) THEN 1 ELSE 0 END;
    DECLARE @IsDocumentEditor BIT = CASE WHEN EXISTS (SELECT 1 FROM [app].[DocumentPermission]
                                                      WHERE [DocumentId] = @DocumentId AND [UserId] = @UserId AND [Role] = 1 AND [LogicalNodeId] IS NULL) THEN 1 ELSE 0 END;
    DECLARE @Active BIT = CASE WHEN @Exists = 1 AND @Deleted = 0 THEN 1 ELSE 0 END;
    DECLARE @CanEditAllContent BIT = CASE WHEN @Active = 1 AND (@IsOwner = 1 OR @IsDocumentEditor = 1) THEN 1 ELSE 0 END;

    -- The version whose tree resolves node grants: the given one if it belongs to the document, else the draft or current version.
    DECLARE @VersionId INT = (SELECT [Id] FROM [app].[DocumentVersion] WHERE [Id] = @DocumentVersionId AND [DocumentId] = @DocumentId);
    IF @VersionId IS NULL AND @DocumentVersionId IS NULL
        SET @VersionId = (SELECT TOP (1) [Id] FROM [app].[DocumentVersion]
                          WHERE [DocumentId] = @DocumentId AND ([Status] = 1 OR [IsCurrent] = 1)
                          ORDER BY CASE WHEN [Status] = 1 THEN 0 ELSE 1 END, [Id] DESC);

    SELECT
        @IsOwner AS [IsOwner],
        @IsAdmin AS [IsAdmin],
        @IsEditor AS [IsEditor],
        @IsApprover AS [IsApprover],
        CAST(CASE WHEN @Exists = 1 AND (@Deleted = 0 OR @IsOwner = 1 OR @IsAdmin = 1) THEN 1 ELSE 0 END AS BIT) AS [CanView],
        CAST(@Active & @IsOwner AS BIT) AS [CanEditStructure],
        @CanEditAllContent AS [CanEditAllContent],
        CAST(CASE WHEN @Active = 1 AND (@IsOwner = 1 OR @IsEditor = 1 OR @IsApprover = 1) THEN 1 ELSE 0 END AS BIT) AS [CanComment],
        CAST(CASE WHEN @Active = 1 AND (@IsOwner = 1 OR @IsApprover = 1) THEN 1 ELSE 0 END AS BIT) AS [CanResolve],
        CAST(CASE WHEN @Active = 1 AND @IsOwner = 0 AND @IsApprover = 1 THEN 1 ELSE 0 END AS BIT) AS [CanSign],
        CAST(@Active & @IsOwner AS BIT) AS [CanManage],
        CAST(CASE WHEN @Exists = 1 AND (@IsAdmin = 1 OR (@IsOwner = 1 AND @Deleted = 0)) THEN 1 ELSE 0 END AS BIT) AS [CanMove],
        CAST(CASE WHEN @Exists = 1 AND (@IsAdmin = 1 OR @IsOwner = 1) THEN 1 ELSE 0 END AS BIT) AS [CanRestore],
        @VersionId AS [DocumentVersionId];

    -- Node grants expanded to descendants (the same tree walk as usp_CheckPermission, top-down).
    WITH [editable] AS (
        SELECT [n].[Id], [n].[LogicalNodeId], 0 AS [Depth]
        FROM [app].[DocumentNode] AS [n]
        JOIN [app].[DocumentPermission] AS [g]
            ON [g].[DocumentId] = @DocumentId AND [g].[UserId] = @UserId AND [g].[Role] = 1 AND [g].[LogicalNodeId] = [n].[LogicalNodeId]
        WHERE [n].[DocumentVersionId] = @VersionId AND @Active = 1 AND @CanEditAllContent = 0
        UNION ALL
        SELECT [c].[Id], [c].[LogicalNodeId], [e].[Depth] + 1
        FROM [app].[DocumentNode] AS [c]
        JOIN [editable] AS [e] ON [c].[DocumentVersionId] = @VersionId AND [c].[ParentNodeId] = [e].[Id]
        WHERE [e].[Depth] < 100)
    SELECT DISTINCT [LogicalNodeId]
    FROM [editable]
    OPTION (MAXRECURSION 200);
END;
