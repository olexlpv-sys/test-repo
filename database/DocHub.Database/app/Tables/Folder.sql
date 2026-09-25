CREATE TABLE [app].[Folder]
(
    [Id]              INT            IDENTITY (1, 1) NOT NULL,
    [ParentFolderId]  INT            NULL,
    [Name]            NVARCHAR (200) NOT NULL,
    [SortOrder]       INT            NOT NULL,
    [CreatedAt]       DATETIME2 (3)  NOT NULL CONSTRAINT [DF_Folder_CreatedAt] DEFAULT (SYSUTCDATETIME()),
    [CreatedByUserId] INT            NOT NULL,
    [RowVersion]      ROWVERSION     NOT NULL,
    CONSTRAINT [PK_Folder] PRIMARY KEY CLUSTERED ([Id]),
    CONSTRAINT [FK_Folder_Parent] FOREIGN KEY ([ParentFolderId]) REFERENCES [app].[Folder] ([Id]),
    CONSTRAINT [FK_Folder_CreatedBy] FOREIGN KEY ([CreatedByUserId]) REFERENCES [app].[User] ([Id]),
    CONSTRAINT [CK_Folder_Name_NotEmpty] CHECK (LEN(LTRIM(RTRIM([Name]))) > 0),
    CONSTRAINT [CK_Folder_NotOwnParent] CHECK ([ParentFolderId] IS NULL OR [ParentFolderId] <> [Id])
);
GO
-- Folder names are unique among siblings (case-insensitive by collation).
CREATE UNIQUE NONCLUSTERED INDEX [UX_Folder_Parent_Name]
    ON [app].[Folder] ([ParentFolderId], [Name]) WHERE [ParentFolderId] IS NOT NULL;
GO
CREATE UNIQUE NONCLUSTERED INDEX [UX_Folder_Root_Name]
    ON [app].[Folder] ([Name]) WHERE [ParentFolderId] IS NULL;
