/*
Signed versions flagged "modified after signing" (FR-H5, T07 rule 4): the triggers set VersionStamp.TamperedAt, or
reconciliation found a change committed at or after the version's ledger signing transaction (T21 — so a cleared stamp keeps
the flag; findings from before signing don't count). Only Signed versions carry the flag.
*/
CREATE VIEW [audit].[vModifiedAfterSigning]
AS
SELECT [v].[Id] AS [DocumentVersionId], [v].[DocumentId]
FROM [app].[DocumentVersion] AS [v]
LEFT JOIN [app].[VersionStamp] AS [s] ON [s].[DocumentVersionId] = [v].[Id]
WHERE [v].[Status] = 2
  AND ([s].[TamperedAt] IS NOT NULL
       OR EXISTS (SELECT 1 FROM [audit].[ReconciliationFinding] AS [f] WHERE [f].[DocumentVersionId] = [v].[Id] AND [f].[AfterSigning] = 1));
