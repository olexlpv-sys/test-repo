-- Temporal + updatable ledger table (T21): immutable history, FOR SYSTEM_TIME queries, principal of every transaction.
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
    [ValidFrom]         DATETIME2 (7)    GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
    [ValidTo]           DATETIME2 (7)    GENERATED ALWAYS AS ROW END HIDDEN NOT NULL,
    PERIOD FOR SYSTEM_TIME ([ValidFrom], [ValidTo]),
    CONSTRAINT [PK_Document] PRIMARY KEY CLUSTERED ([Id]),
    CONSTRAINT [FK_Document_Folder] FOREIGN KEY ([FolderId]) REFERENCES [app].[Folder] ([Id]),
    CONSTRAINT [FK_Document_Owner] FOREIGN KEY ([OwnerUserId]) REFERENCES [app].[User] ([Id]),
    CONSTRAINT [FK_Document_DeletedBy] FOREIGN KEY ([DeletedByUserId]) REFERENCES [app].[User] ([Id]),
    CONSTRAINT [CK_Document_Title_NotEmpty] CHECK (LEN(LTRIM(RTRIM([Title]))) > 0),
    CONSTRAINT [CK_Document_Deleted] CHECK (([DeletedAt] IS NULL AND [DeletedByUserId] IS NULL) OR ([DeletedAt] IS NOT NULL AND [DeletedByUserId] IS NOT NULL))
)
WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = [history].[Document]), LEDGER = ON (LEDGER_VIEW = [app].[Document_Ledger] (TRANSACTION_ID_COLUMN_NAME = [ledger_transaction_id], SEQUENCE_NUMBER_COLUMN_NAME = [ledger_sequence_number], OPERATION_TYPE_COLUMN_NAME = [ledger_operation_type], OPERATION_TYPE_DESC_COLUMN_NAME = [ledger_operation_type_desc])));
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
