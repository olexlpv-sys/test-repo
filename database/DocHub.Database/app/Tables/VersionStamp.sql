-- Per-version change stamp, maintained by the audit triggers (T03); not audited itself.
CREATE TABLE [app].[VersionStamp]
(
    [DocumentVersionId] INT           NOT NULL,
    -- audit.ChangeLog.Id of the latest change to the version (cache key and ETag, NFR-L9).
    [LastChangeLogId]   BIGINT        NOT NULL,
    -- First change to nodes/content of a Signed version after signing (FR-H5).
    [TamperedAt]        DATETIME2 (3) NULL,
    CONSTRAINT [PK_VersionStamp] PRIMARY KEY CLUSTERED ([DocumentVersionId]),
    CONSTRAINT [FK_VersionStamp_Version] FOREIGN KEY ([DocumentVersionId]) REFERENCES [app].[DocumentVersion] ([Id])
);
