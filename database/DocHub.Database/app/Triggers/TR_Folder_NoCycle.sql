/*
Folders form a tree (FR-F2): rejects any insert/update that makes a folder its own ancestor — from the API (which also
checks) or from a support script. CK_Folder_NotOwnParent covers the direct self-reference only.
*/
CREATE TRIGGER [app].[TR_Folder_NoCycle]
ON [app].[Folder]
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;

    IF NOT UPDATE([ParentFolderId])
        RETURN;

    -- Walk up from every changed folder; reaching the folder again is a cycle.
    DECLARE @Cycle BIT = 0;
    WITH [up] AS (
        SELECT [i].[Id] AS [StartId], [i].[ParentFolderId] AS [AncestorId], 1 AS [Depth]
        FROM inserted AS [i]
        WHERE [i].[ParentFolderId] IS NOT NULL
        UNION ALL
        SELECT [u].[StartId], [f].[ParentFolderId], [u].[Depth] + 1
        FROM [up] AS [u]
        JOIN [app].[Folder] AS [f] ON [f].[Id] = [u].[AncestorId]
        WHERE [f].[ParentFolderId] IS NOT NULL AND [u].[AncestorId] <> [u].[StartId] AND [u].[Depth] < 10000)
    SELECT TOP (1) @Cycle = 1 FROM [up] WHERE [AncestorId] = [StartId]
    OPTION (MAXRECURSION 10000);

    IF @Cycle = 1
        THROW 50040, N'A folder can''t be moved into itself or into one of its sub-folders.', 1;
END;
