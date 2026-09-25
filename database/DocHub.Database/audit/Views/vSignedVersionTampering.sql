-- Audit rows that changed nodes or content of a Signed version after it was signed (FR-H5).
CREATE VIEW [audit].[vSignedVersionTampering]
AS
SELECT
    [l].[Id] AS [ChangeLogId],
    [l].[ChangedAt],
    [l].[TableName],
    [l].[Operation],
    [l].[EntityId],
    [v].[DocumentId],
    [l].[DocumentVersionId],
    [v].[VersionNumber],
    [v].[SignedAt],
    [l].[LogicalNodeId],
    [l].[Source],
    [l].[DbLogin],
    [l].[UserId],
    [l].[Ticket],
    [l].[Reason]
FROM [audit].[ChangeLog] AS [l]
JOIN [app].[DocumentVersion] AS [v] ON [v].[Id] = [l].[DocumentVersionId]
WHERE [v].[Status] = 2
  AND [l].[ChangedAt] > [v].[SignedAt]
  AND [l].[TableName] IN (N'app.DocumentNode', N'app.NodeContent');
