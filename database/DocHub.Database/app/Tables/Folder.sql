-- Temporal + updatable ledger table (T21): immutable history, FOR SYSTEM_TIME queries, principal of every transaction.
CREATE TABLE [app].[Folder]
(
    [Id]              INT            IDENTITY (1, 1) NOT NULL,
    [ParentFolderId]  INT            NULL,
    [Name]            NVARCHAR (200) NOT NULL,
    [SortOrder]       INT            NOT NULL,
    [CreatedAt]       DATETIME2 (3)  NOT NULL CONSTRAINT [DF_Folder_CreatedAt] DEFAULT (SYSUTCDATETIME()),
    [CreatedByUserId] INT            NOT NULL,
    [RowVersion]      ROWVERSION     NOT NULL,
    [ValidFrom]         DATETIME2 (7)    GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
    [ValidTo]           DATETIME2 (7)    GENERATED ALWAYS AS ROW END HIDDEN NOT NULL,
    PERIOD FOR SYSTEM_TIME ([ValidFrom], [ValidTo]),
    CONSTRAINT [PK_Folder] PRIMARY KEY CLUSTERED ([Id]),
    CONSTRAINT [FK_Folder_Parent] FOREIGN KEY ([ParentFolderId]) REFERENCES [app].[Folder] ([Id]),
    CONSTRAINT [FK_Folder_CreatedBy] FOREIGN KEY ([CreatedByUserId]) REFERENCES [app].[User] ([Id]),
    CONSTRAINT [CK_Folder_Name_NotEmpty] CHECK (LEN(LTRIM(RTRIM([Name]))) > 0),
    CONSTRAINT [CK_Folder_NotOwnParent] CHECK ([ParentFolderId] IS NULL OR [ParentFolderId] <> [Id])
)
WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = [history].[Folder]), LEDGER = ON (LEDGER_VIEW = [app].[Folder_Ledger] (TRANSACTION_ID_COLUMN_NAME = [ledger_transaction_id], SEQUENCE_NUMBER_COLUMN_NAME = [ledger_sequence_number], OPERATION_TYPE_COLUMN_NAME = [ledger_operation_type], OPERATION_TYPE_DESC_COLUMN_NAME = [ledger_operation_type_desc])));
GO
-- Folder names are unique among siblings (case-insensitive by collation).
CREATE UNIQUE NONCLUSTERED INDEX [UX_Folder_Parent_Name]
    ON [app].[Folder] ([ParentFolderId], [Name]) WHERE [ParentFolderId] IS NOT NULL;
GO
CREATE UNIQUE NONCLUSTERED INDEX [UX_Folder_Root_Name]
    ON [app].[Folder] ([Name]) WHERE [ParentFolderId] IS NULL;
