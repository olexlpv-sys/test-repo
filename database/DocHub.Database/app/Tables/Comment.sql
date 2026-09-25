-- Temporal + updatable ledger table (T21): immutable history, FOR SYSTEM_TIME queries, principal of every transaction.
CREATE TABLE [app].[Comment]
(
    [Id]                INT              IDENTITY (1, 1) NOT NULL,
    [DocumentId]        INT              NOT NULL,
    [DocumentVersionId] INT              NOT NULL,
    -- NULL = comment on the whole document.
    [LogicalNodeId]     UNIQUEIDENTIFIER NULL,
    [ParentCommentId]   INT              NULL,
    [AuthorUserId]      INT              NOT NULL,
    [Body]              NVARCHAR (4000)  NOT NULL,
    [CreatedAt]         DATETIME2 (3)    NOT NULL CONSTRAINT [DF_Comment_CreatedAt] DEFAULT (SYSUTCDATETIME()),
    [EditedAt]          DATETIME2 (3)    NULL,
    [DeletedAt]         DATETIME2 (3)    NULL,
    [ResolvedAt]        DATETIME2 (3)    NULL,
    [ResolvedByUserId]  INT              NULL,
    [RowVersion]        ROWVERSION       NOT NULL,
    [ValidFrom]         DATETIME2 (7)    GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
    [ValidTo]           DATETIME2 (7)    GENERATED ALWAYS AS ROW END HIDDEN NOT NULL,
    PERIOD FOR SYSTEM_TIME ([ValidFrom], [ValidTo]),
    CONSTRAINT [PK_Comment] PRIMARY KEY CLUSTERED ([Id]),
    CONSTRAINT [FK_Comment_Document] FOREIGN KEY ([DocumentId]) REFERENCES [app].[Document] ([Id]),
    CONSTRAINT [FK_Comment_Version] FOREIGN KEY ([DocumentVersionId]) REFERENCES [app].[DocumentVersion] ([Id]),
    CONSTRAINT [FK_Comment_Parent] FOREIGN KEY ([ParentCommentId]) REFERENCES [app].[Comment] ([Id]),
    CONSTRAINT [FK_Comment_Author] FOREIGN KEY ([AuthorUserId]) REFERENCES [app].[User] ([Id]),
    CONSTRAINT [FK_Comment_ResolvedBy] FOREIGN KEY ([ResolvedByUserId]) REFERENCES [app].[User] ([Id]),
    CONSTRAINT [CK_Comment_Body_NotEmpty] CHECK (LEN(LTRIM(RTRIM([Body]))) > 0),
    CONSTRAINT [CK_Comment_Resolved] CHECK (([ResolvedAt] IS NULL AND [ResolvedByUserId] IS NULL) OR ([ResolvedAt] IS NOT NULL AND [ResolvedByUserId] IS NOT NULL)),
    CONSTRAINT [CK_Comment_NotOwnParent] CHECK ([ParentCommentId] IS NULL OR [ParentCommentId] <> [Id])
)
WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = [history].[Comment]), LEDGER = ON (LEDGER_VIEW = [app].[Comment_Ledger] (TRANSACTION_ID_COLUMN_NAME = [ledger_transaction_id], SEQUENCE_NUMBER_COLUMN_NAME = [ledger_sequence_number], OPERATION_TYPE_COLUMN_NAME = [ledger_operation_type], OPERATION_TYPE_DESC_COLUMN_NAME = [ledger_operation_type_desc])));
GO
CREATE NONCLUSTERED INDEX [IX_Comment_Version_Node]
    ON [app].[Comment] ([DocumentVersionId], [LogicalNodeId]);
GO
CREATE NONCLUSTERED INDEX [IX_Comment_Parent]
    ON [app].[Comment] ([ParentCommentId]) WHERE [ParentCommentId] IS NOT NULL;
