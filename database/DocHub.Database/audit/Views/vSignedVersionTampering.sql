/*
Changes to Signed versions made after signing (FR-H5): every node, content and version-row change of a version whose
VersionStamp.TamperedAt is set, from the first tampering on. A row that moved a node/content out of a version is matched
through its old version id (OldValues).
*/
CREATE VIEW [audit].[vSignedVersionTampering]
AS
SELECT
    [l].[Id] AS [ChangeLogId],
    [l].[ChangedAt],
    [l].[TableName],
    [l].[Operation],
    [l].[EntityId],
    [v].[DocumentId],
    [v].[Id] AS [DocumentVersionId],
    [v].[VersionNumber],
    [v].[SignedAt],
    [s].[TamperedAt],
    [l].[LogicalNodeId],
    [l].[ChangedColumns],
    [l].[Source],
    [l].[DbLogin],
    [l].[UserId],
    [l].[Ticket],
    [l].[Reason]
FROM [app].[VersionStamp] AS [s]
JOIN [app].[DocumentVersion] AS [v] ON [v].[Id] = [s].[DocumentVersionId]
JOIN [audit].[ChangeLog] AS [l]
    ON [l].[TableName] IN (N'app.DocumentNode', N'app.NodeContent', N'app.DocumentVersion')
   AND ([l].[DocumentVersionId] = [v].[Id] OR TRY_CONVERT(INT, JSON_VALUE([l].[OldValues], N'$.DocumentVersionId')) = [v].[Id]
        OR ([l].[TableName] = N'app.DocumentVersion' AND [l].[EntityId] = [v].[Id]))
   AND [l].[ChangedAt] >= [s].[TamperedAt]
WHERE [s].[TamperedAt] IS NOT NULL;
