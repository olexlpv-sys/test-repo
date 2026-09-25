/*
Deletes a node with its whole subtree (FR-T2, FR-D3, T08): contents and style usage go by cascade; the audit triggers log
every deleted node and content row. Returns one row with DeletedCount (nodes). Callers check editability and permissions.
*/
CREATE PROCEDURE [app].[usp_DeleteSubtree]
    @NodeId INT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @VersionId INT = (SELECT [DocumentVersionId] FROM [app].[DocumentNode] WHERE [Id] = @NodeId);
    IF @VersionId IS NULL
        THROW 50050, N'The node does not exist.', 1;

    CREATE TABLE [#Subtree] ([Id] INT NOT NULL PRIMARY KEY);
    WITH [subtree] AS (
        SELECT [Id] FROM [app].[DocumentNode] WHERE [Id] = @NodeId
        UNION ALL
        SELECT [n].[Id] FROM [app].[DocumentNode] AS [n] JOIN [subtree] AS [s] ON [n].[DocumentVersionId] = @VersionId AND [n].[ParentNodeId] = [s].[Id])
    INSERT INTO [#Subtree] ([Id]) SELECT [Id] FROM [subtree] OPTION (MAXRECURSION 0);

    BEGIN TRANSACTION;
    -- One statement: the self-referencing parent FK is checked after it, so parents and children go together.
    DELETE [n] FROM [app].[DocumentNode] AS [n] JOIN [#Subtree] AS [s] ON [s].[Id] = [n].[Id];
    DECLARE @Deleted INT = @@ROWCOUNT;
    COMMIT TRANSACTION;

    SELECT @Deleted AS [DeletedCount];
END;
