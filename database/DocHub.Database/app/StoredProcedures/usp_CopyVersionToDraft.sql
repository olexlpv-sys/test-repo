/*
New draft = deep copy of a version (FR-V3, FR-D3, T07 rule 3), set-based in one transaction: the version row (Draft,
BasedOnVersionId = source, IsCurrent moved to it), all nodes with their LogicalNodeId (parents remapped), their content with
the derived columns, and the style usage. OperationContext 'CopyVersion' marks the audit rows of the copy (T11 collapses
them); the caller's session context is restored afterwards. Returns the new version id (one row, NewVersionId).
Callers check permissions and the source (latest signed version); a second draft fails on UX_DocumentVersion_OneDraft.
*/
CREATE PROCEDURE [app].[usp_CopyVersionToDraft]
    @SourceVersionId INT,
    @UserId          INT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @DocumentId INT = (SELECT [DocumentId] FROM [app].[DocumentVersion] WHERE [Id] = @SourceVersionId);
    IF @DocumentId IS NULL
        THROW 50030, N'The source version does not exist.', 1;

    DECLARE @PreviousContext NVARCHAR (50) = TRY_CONVERT(NVARCHAR (50), SESSION_CONTEXT(N'OperationContext'));
    EXEC sys.sp_set_session_context @key = N'OperationContext', @value = N'CopyVersion';

    BEGIN TRY
        BEGIN TRANSACTION;

        UPDATE [app].[DocumentVersion] SET [IsCurrent] = 0 WHERE [DocumentId] = @DocumentId AND [IsCurrent] = 1;

        DECLARE @NewVersionId INT;
        INSERT INTO [app].[DocumentVersion] ([DocumentId], [Status], [BasedOnVersionId], [CreatedByUserId], [IsCurrent])
        VALUES (@DocumentId, 1, @SourceVersionId, @UserId, 1);
        SET @NewVersionId = SCOPE_IDENTITY();

        -- Nodes are copied level by level, so every parent already has its new id: no second pass (and no second set of
        -- audit rows) for the parent links. Old → new ids via MERGE … OUTPUT (the audit trigger forbids OUTPUT without INTO).
        CREATE TABLE [#Source] ([Id] INT NOT NULL PRIMARY KEY, [ParentNodeId] INT NULL, [Depth] INT NOT NULL);
        WITH [levels] AS (
            SELECT [Id], [ParentNodeId], 0 AS [Depth] FROM [app].[DocumentNode] WHERE [DocumentVersionId] = @SourceVersionId AND [ParentNodeId] IS NULL
            UNION ALL
            SELECT [n].[Id], [n].[ParentNodeId], [l].[Depth] + 1
            FROM [app].[DocumentNode] AS [n]
            JOIN [levels] AS [l] ON [n].[ParentNodeId] = [l].[Id]
            WHERE [n].[DocumentVersionId] = @SourceVersionId)
        INSERT INTO [#Source] ([Id], [ParentNodeId], [Depth]) SELECT [Id], [ParentNodeId], [Depth] FROM [levels] OPTION (MAXRECURSION 0);

        CREATE TABLE [#Map] ([OldId] INT NOT NULL PRIMARY KEY, [NewId] INT NOT NULL, [LogicalNodeId] UNIQUEIDENTIFIER NOT NULL);
        DECLARE @Depth INT = 0, @MaxDepth INT = (SELECT MAX([Depth]) FROM [#Source]);
        WHILE @Depth <= @MaxDepth
        BEGIN
            MERGE [app].[DocumentNode] AS [target]
            USING (SELECT [n].[Id], [n].[LogicalNodeId], [n].[NodeTypeId], [n].[Title], [n].[SortOrder], [p].[NewId] AS [NewParentId]
                   FROM [#Source] AS [s]
                   JOIN [app].[DocumentNode] AS [n] ON [n].[Id] = [s].[Id]
                   LEFT JOIN [#Map] AS [p] ON [p].[OldId] = [s].[ParentNodeId]
                   WHERE [s].[Depth] = @Depth) AS [source]
            ON 1 = 0
            WHEN NOT MATCHED THEN
                INSERT ([DocumentVersionId], [LogicalNodeId], [ParentNodeId], [NodeTypeId], [Title], [SortOrder], [CreatedByUserId], [ModifiedByUserId])
                VALUES (@NewVersionId, [source].[LogicalNodeId], [source].[NewParentId], [source].[NodeTypeId], [source].[Title], [source].[SortOrder], @UserId, @UserId)
            OUTPUT [source].[Id], INSERTED.[Id], INSERTED.[LogicalNodeId] INTO [#Map] ([OldId], [NewId], [LogicalNodeId]);
            SET @Depth += 1;
        END;

        INSERT INTO [app].[NodeContent]
            ([NodeId], [DocumentVersionId], [LogicalNodeId], [SchemaVersion], [ContentJson], [ContentHtml], [PlainText], [ContentHash], [DerivedStale], [ModifiedByUserId])
        SELECT [m].[NewId], @NewVersionId, [m].[LogicalNodeId], [c].[SchemaVersion], [c].[ContentJson], [c].[ContentHtml], [c].[PlainText], [c].[ContentHash], [c].[DerivedStale], @UserId
        FROM [app].[NodeContent] AS [c]
        JOIN [#Map] AS [m] ON [m].[OldId] = [c].[NodeId];

        INSERT INTO [app].[ContentStyleUsage] ([StyleId], [NodeId])
        SELECT [u].[StyleId], [m].[NewId]
        FROM [app].[ContentStyleUsage] AS [u]
        JOIN [#Map] AS [m] ON [m].[OldId] = [u].[NodeId];

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
        EXEC sys.sp_set_session_context @key = N'OperationContext', @value = @PreviousContext;
        THROW;
    END CATCH;

    EXEC sys.sp_set_session_context @key = N'OperationContext', @value = @PreviousContext;
    SELECT @NewVersionId AS [NewVersionId];
END;
