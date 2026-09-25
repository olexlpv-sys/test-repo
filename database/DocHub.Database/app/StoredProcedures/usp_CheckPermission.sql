/*
Single permission check (FR-D2): returns one row with Allowed (bit). Actions:
  View          any user for a non-deleted document; owner or admin for a deleted one (FR-P5)
  Manage        owner: lifecycle (new draft, discard, delete, rename) and roles
  EditStructure owner (FR-P1)
  EditContent   owner, document-level editor, or editor of @LogicalNodeId or one of its ancestors in @DocumentVersionId (FR-P2)
  Comment       owner, editor or approver
  Resolve       owner or approver
  Sign          approver (FR-P3); the owner is never an approver
  Move          owner (active document) or admin (also deleted documents)
  Restore       owner or admin
Every action except View/Move/Restore requires a non-deleted document. Unknown actions raise an error.
*/
CREATE PROCEDURE [app].[usp_CheckPermission]
    @DocumentId        INT,
    @UserId            INT,
    @Action            VARCHAR (20),
    @DocumentVersionId INT              = NULL,
    @LogicalNodeId     UNIQUEIDENTIFIER = NULL
AS
BEGIN
    SET NOCOUNT ON;

    IF @Action NOT IN ('View', 'Manage', 'EditStructure', 'EditContent', 'Comment', 'Resolve', 'Sign', 'Move', 'Restore')
        THROW 50010, N'Unknown permission action.', 1;

    IF @Action = 'EditContent' AND @LogicalNodeId IS NOT NULL AND @DocumentVersionId IS NULL
        THROW 50011, N'EditContent on a node requires @DocumentVersionId.', 1;

    DECLARE @OwnerUserId INT, @Deleted BIT;
    SELECT @OwnerUserId = [OwnerUserId], @Deleted = CASE WHEN [DeletedAt] IS NULL THEN 0 ELSE 1 END
    FROM [app].[Document]
    WHERE [Id] = @DocumentId;

    DECLARE @IsAdmin BIT = COALESCE((SELECT [IsAdmin] FROM [app].[User] WHERE [Id] = @UserId AND [IsActive] = 1), 0);
    DECLARE @IsOwner BIT = CASE WHEN @OwnerUserId = @UserId THEN 1 ELSE 0 END;
    DECLARE @IsApprover BIT = CASE WHEN EXISTS (SELECT 1 FROM [app].[DocumentPermission]
                                                WHERE [DocumentId] = @DocumentId AND [UserId] = @UserId AND [Role] = 2) THEN 1 ELSE 0 END;
    DECLARE @IsEditor BIT = CASE WHEN EXISTS (SELECT 1 FROM [app].[DocumentPermission]
                                              WHERE [DocumentId] = @DocumentId AND [UserId] = @UserId AND [Role] = 1) THEN 1 ELSE 0 END;

    DECLARE @Allowed BIT = CASE
        WHEN @OwnerUserId IS NULL THEN 0
        WHEN @Action = 'View' THEN CASE WHEN @Deleted = 0 OR @IsOwner = 1 OR @IsAdmin = 1 THEN 1 ELSE 0 END
        WHEN @Action = 'Move' THEN CASE WHEN @IsAdmin = 1 OR (@IsOwner = 1 AND @Deleted = 0) THEN 1 ELSE 0 END
        WHEN @Action = 'Restore' THEN CASE WHEN @IsAdmin = 1 OR @IsOwner = 1 THEN 1 ELSE 0 END
        WHEN @Deleted = 1 THEN 0
        WHEN @Action IN ('Manage', 'EditStructure') THEN @IsOwner
        WHEN @Action = 'Comment' THEN CASE WHEN @IsOwner = 1 OR @IsEditor = 1 OR @IsApprover = 1 THEN 1 ELSE 0 END
        WHEN @Action = 'Resolve' THEN CASE WHEN @IsOwner = 1 OR @IsApprover = 1 THEN 1 ELSE 0 END
        WHEN @Action = 'Sign' THEN CASE WHEN @IsOwner = 0 AND @IsApprover = 1 THEN 1 ELSE 0 END
        ELSE NULL
    END;

    IF @Action = 'EditContent' AND @OwnerUserId IS NOT NULL AND @Deleted = 0
    BEGIN
        SET @Allowed = CASE
            WHEN @IsOwner = 1 THEN 1
            WHEN EXISTS (SELECT 1 FROM [app].[DocumentPermission]
                         WHERE [DocumentId] = @DocumentId AND [UserId] = @UserId AND [Role] = 1 AND [LogicalNodeId] IS NULL) THEN 1
            ELSE 0
        END;

        -- Node-level editor grants cover the node and its descendants in the given version's tree.
        IF @Allowed = 0 AND @LogicalNodeId IS NOT NULL
        BEGIN
            WITH [ancestors] AS (
                SELECT [n].[Id], [n].[ParentNodeId], [n].[LogicalNodeId], 0 AS [Depth]
                FROM [app].[DocumentNode] AS [n]
                WHERE [n].[DocumentVersionId] = @DocumentVersionId AND [n].[LogicalNodeId] = @LogicalNodeId
                UNION ALL
                SELECT [p].[Id], [p].[ParentNodeId], [p].[LogicalNodeId], [a].[Depth] + 1
                FROM [app].[DocumentNode] AS [p]
                JOIN [ancestors] AS [a] ON [p].[DocumentVersionId] = @DocumentVersionId AND [p].[Id] = [a].[ParentNodeId]
                WHERE [a].[Depth] < 100)
            SELECT @Allowed = CASE WHEN EXISTS (
                SELECT 1 FROM [ancestors] AS [a]
                JOIN [app].[DocumentPermission] AS [g]
                    ON [g].[DocumentId] = @DocumentId AND [g].[UserId] = @UserId AND [g].[Role] = 1 AND [g].[LogicalNodeId] = [a].[LogicalNodeId])
                THEN 1 ELSE 0 END;
        END;
    END;

    SELECT CAST(COALESCE(@Allowed, 0) AS BIT) AS [Allowed];
END;
