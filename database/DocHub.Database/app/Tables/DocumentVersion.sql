CREATE TABLE [app].[DocumentVersion]
(
    [Id]                INT            IDENTITY (1, 1) NOT NULL,
    [DocumentId]        INT            NOT NULL,
    -- 1 Draft, 2 Signed, 3 Deleted (discarded draft)
    [Status]            TINYINT        NOT NULL,
    [VersionNumber]     INT            NULL,
    [BasedOnVersionId]  INT            NULL,
    [CreatedAt]         DATETIME2 (3)  NOT NULL CONSTRAINT [DF_DocumentVersion_CreatedAt] DEFAULT (SYSUTCDATETIME()),
    [CreatedByUserId]   INT            NOT NULL,
    [SignedAt]          DATETIME2 (3)  NULL,
    [SignedContentHash] VARBINARY (32) NULL,
    [IsCurrent]         BIT            NOT NULL CONSTRAINT [DF_DocumentVersion_IsCurrent] DEFAULT (0),
    [RowVersion]        ROWVERSION     NOT NULL,
    CONSTRAINT [PK_DocumentVersion] PRIMARY KEY CLUSTERED ([Id]),
    CONSTRAINT [FK_DocumentVersion_Document] FOREIGN KEY ([DocumentId]) REFERENCES [app].[Document] ([Id]),
    CONSTRAINT [FK_DocumentVersion_BasedOn] FOREIGN KEY ([BasedOnVersionId]) REFERENCES [app].[DocumentVersion] ([Id]),
    CONSTRAINT [FK_DocumentVersion_CreatedBy] FOREIGN KEY ([CreatedByUserId]) REFERENCES [app].[User] ([Id]),
    CONSTRAINT [CK_DocumentVersion_Status] CHECK ([Status] >= 1 AND [Status] <= 3),
    CONSTRAINT [CK_DocumentVersion_VersionNumber_Positive] CHECK ([VersionNumber] IS NULL OR [VersionNumber] > 0),
    -- Signed <=> version number and signing time are set.
    CONSTRAINT [CK_DocumentVersion_Signed] CHECK (
        ([Status] = 2 AND [VersionNumber] IS NOT NULL AND [SignedAt] IS NOT NULL)
        OR ([Status] <> 2 AND [VersionNumber] IS NULL AND [SignedAt] IS NULL)),
    -- A discarded draft is never the current version.
    CONSTRAINT [CK_DocumentVersion_DeletedNotCurrent] CHECK ([Status] <> 3 OR [IsCurrent] = 0)
);
GO
-- At most one draft per document.
CREATE UNIQUE NONCLUSTERED INDEX [UX_DocumentVersion_OneDraft]
    ON [app].[DocumentVersion] ([DocumentId]) WHERE [Status] = 1;
GO
CREATE UNIQUE NONCLUSTERED INDEX [UX_DocumentVersion_VersionNumber]
    ON [app].[DocumentVersion] ([DocumentId], [VersionNumber]) WHERE [VersionNumber] IS NOT NULL;
GO
-- At most one current version per document (NFR-L8).
CREATE UNIQUE NONCLUSTERED INDEX [UX_DocumentVersion_Current]
    ON [app].[DocumentVersion] ([DocumentId]) WHERE [IsCurrent] = 1;
GO
CREATE NONCLUSTERED INDEX [IX_DocumentVersion_Document]
    ON [app].[DocumentVersion] ([DocumentId]) INCLUDE ([Status], [VersionNumber], [IsCurrent]);
