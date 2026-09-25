-- Cache of a version's canonical content hash (T07 rule 2), computed by the API from the tree and ContentJson. Valid only
-- while ContentChangeLogId equals VersionStamp.ContentChangeLogId (0 = the version has no nodes yet); the document list
-- uses it for the signature progress. Derived data: not audited, and support scripts can't write it.
CREATE TABLE [app].[VersionContentHash]
(
    [DocumentVersionId]  INT            NOT NULL,
    [ContentChangeLogId] BIGINT         NOT NULL,
    [ContentHash]        VARBINARY (32) NOT NULL,
    [ComputedAt]         DATETIME2 (3)  NOT NULL CONSTRAINT [DF_VersionContentHash_ComputedAt] DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT [PK_VersionContentHash] PRIMARY KEY CLUSTERED ([DocumentVersionId]),
    CONSTRAINT [FK_VersionContentHash_Version] FOREIGN KEY ([DocumentVersionId]) REFERENCES [app].[DocumentVersion] ([Id]) ON DELETE CASCADE,
    CONSTRAINT [CK_VersionContentHash_ContentHash] CHECK (DATALENGTH([ContentHash]) = 32)
);
