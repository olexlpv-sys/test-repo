/*
The nodes of a version form a tree (FR-T1): rejects inserts/updates that make a node its own ancestor or nest it deeper
than 100 levels — from the API (which also checks) or from a support script. CK_DocumentNode_NotOwnParent covers the
direct self-reference only; the composite FK keeps parents in the same version.
*/
CREATE TRIGGER [app].[TR_DocumentNode_Tree]
ON [app].[DocumentNode]
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;

    IF NOT UPDATE([ParentNodeId])
        RETURN;

    DECLARE @Cycle BIT = 0, @TooDeep BIT = 0;
    DECLARE @IsUpdate BIT = CASE WHEN EXISTS (SELECT 1 FROM deleted) THEN 1 ELSE 0 END;

    -- A cycle needs an existing node to get a new parent: inserted rows can't be anyone's parent yet.
    IF @IsUpdate = 1
    BEGIN
        WITH [up] AS (
            SELECT [i].[Id] AS [StartId], [i].[ParentNodeId] AS [AncestorId], 1 AS [Depth]
            FROM inserted AS [i]
            WHERE [i].[ParentNodeId] IS NOT NULL
            UNION ALL
            SELECT [u].[StartId], [n].[ParentNodeId], [u].[Depth] + 1
            FROM [up] AS [u]
            JOIN [app].[DocumentNode] AS [n] ON [n].[Id] = [u].[AncestorId]
            WHERE [n].[ParentNodeId] IS NOT NULL AND [u].[AncestorId] <> [u].[StartId] AND [u].[Depth] <= 100)
        SELECT @Cycle = COALESCE(MAX(CASE WHEN [AncestorId] = [StartId] THEN 1 ELSE 0 END), 0)
        FROM [up]
        OPTION (MAXRECURSION 200);

        IF @Cycle = 1
            THROW 50051, N'A node can''t be moved into itself or into one of its descendants.', 1;
    END;

    -- Level of the deepest node below each changed node = level of its parent + 1 + height of its subtree. Parent levels
    -- are computed once per distinct parent (a deep copy inserts thousands of rows under a few parents).
    WITH [parents] AS (
        SELECT DISTINCT [ParentNodeId] AS [Id] FROM inserted WHERE [ParentNodeId] IS NOT NULL),
    [up] AS (
        SELECT [p].[Id] AS [StartId], [p].[Id] AS [NodeId], 1 AS [Level] FROM [parents] AS [p]
        UNION ALL
        SELECT [u].[StartId], [n].[ParentNodeId], [u].[Level] + 1 FROM [up] AS [u] JOIN [app].[DocumentNode] AS [n] ON [n].[Id] = [u].[NodeId]
        WHERE [n].[ParentNodeId] IS NOT NULL AND [u].[Level] <= 100),
    [parentLevel] AS (
        SELECT [StartId], MAX([Level]) AS [Level] FROM [up] GROUP BY [StartId]),
    [down] AS (
        SELECT [i].[Id] AS [StartId], [i].[Id], [i].[DocumentVersionId], 0 AS [Height] FROM inserted AS [i] WHERE @IsUpdate = 1
        UNION ALL
        -- Children are in the parent's version (composite FK): seek IX_DocumentNode_Version_Parent_Sort.
        SELECT [d].[StartId], [n].[Id], [n].[DocumentVersionId], [d].[Height] + 1
        FROM [app].[DocumentNode] AS [n] JOIN [down] AS [d] ON [n].[DocumentVersionId] = [d].[DocumentVersionId] AND [n].[ParentNodeId] = [d].[Id]
        WHERE [d].[Height] <= 100),
    [height] AS (
        SELECT [StartId], MAX([Height]) AS [Height] FROM [down] GROUP BY [StartId])
    SELECT @TooDeep = CASE WHEN EXISTS (
        SELECT 1
        FROM inserted AS [i]
        LEFT JOIN [parentLevel] AS [p] ON [p].[StartId] = [i].[ParentNodeId]
        LEFT JOIN [height] AS [h] ON [h].[StartId] = [i].[Id]
        WHERE COALESCE([p].[Level], 0) + 1 + COALESCE([h].[Height], 0) > 100) THEN 1 ELSE 0 END
    OPTION (MAXRECURSION 200);

    IF @TooDeep = 1
        THROW 50052, N'A document tree can have at most 100 levels.', 1;
END;
