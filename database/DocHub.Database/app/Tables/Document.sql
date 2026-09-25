CREATE TABLE [app].[Document]
(
    [Id]              INT            IDENTITY (1, 1) NOT NULL,
    [FolderId]        INT            NOT NULL,
    [Title]           NVARCHAR (300) NOT NULL,
    [OwnerUserId]     INT            NOT NULL,
    [CreatedAt]       DATETIME2 (3)  NOT NULL CONSTRAINT [DF_Document_CreatedAt] DEFAULT (SYSUTCDATETIME()),
    [DeletedAt]       DATETIME2 (3)  NULL,
    [DeletedByUserId] INT            NULL,
    [RowVersion]      ROWVERSION     NOT NULL,
    CONSTRAINT [PK_Document] PRIMARY KEY CLUSTERED ([Id]),
    CONSTRAINT [FK_Document_Folder] FOREIGN KEY ([FolderId]) REFERENCES [app].[Folder] ([Id]),
    CONSTRAINT [FK_Document_Owner] FOREIGN KEY ([OwnerUserId]) REFERENCES [app].[User] ([Id]),
    CONSTRAINT [FK_Document_DeletedBy] FOREIGN KEY ([DeletedByUserId]) REFERENCES [app].[User] ([Id]),
    CONSTRAINT [CK_Document_Title_NotEmpty] CHECK (LEN(LTRIM(RTRIM([Title]))) > 0),
    CONSTRAINT [CK_Document_Deleted] CHECK (([DeletedAt] IS NULL AND [DeletedByUserId] IS NULL) OR ([DeletedAt] IS NOT NULL AND [DeletedByUserId] IS NOT NULL))
);
GO
CREATE NONCLUSTERED INDEX [IX_Document_Folder_Active]
    ON [app].[Document] ([FolderId]) INCLUDE ([Title], [OwnerUserId]) WHERE [DeletedAt] IS NULL;
GO
-- Folder delete checks (any document, deleted ones included) and owner lookups.
CREATE NONCLUSTERED INDEX [IX_Document_Folder]
    ON [app].[Document] ([FolderId]);
GO
CREATE NONCLUSTERED INDEX [IX_Document_Owner]
    ON [app].[Document] ([OwnerUserId]);
