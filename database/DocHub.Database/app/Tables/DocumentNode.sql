-- Temporal + updatable ledger table (T21): immutable history, FOR SYSTEM_TIME queries, principal of every transaction.
CREATE TABLE [app].[DocumentNode]
(
    [Id]                INT              IDENTITY (1, 1) NOT NULL,
    [DocumentVersionId] INT              NOT NULL,
    [LogicalNodeId]     UNIQUEIDENTIFIER NOT NULL,
    [ParentNodeId]      INT              NULL,
    [NodeTypeId]        INT              NOT NULL,
    [Title]             NVARCHAR (500)   NOT NULL,
    [SortOrder]         INT              NOT NULL,
    [CreatedAt]         DATETIME2 (3)    NOT NULL CONSTRAINT [DF_DocumentNode_CreatedAt] DEFAULT (SYSUTCDATETIME()),
    [CreatedByUserId]   INT              NOT NULL,
    [ModifiedAt]        DATETIME2 (3)    NOT NULL CONSTRAINT [DF_DocumentNode_ModifiedAt] DEFAULT (SYSUTCDATETIME()),
    [ModifiedByUserId]  INT              NOT NULL,
    [RowVersion]        ROWVERSION       NOT NULL,
    [ValidFrom]         DATETIME2 (7)    GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
    [ValidTo]           DATETIME2 (7)    GENERATED ALWAYS AS ROW END HIDDEN NOT NULL,
    PERIOD FOR SYSTEM_TIME ([ValidFrom], [ValidTo]),
    CONSTRAINT [PK_DocumentNode] PRIMARY KEY CLUSTERED ([Id]),
    CONSTRAINT [UQ_DocumentNode_Version_Id] UNIQUE ([DocumentVersionId], [Id]),
    CONSTRAINT [UQ_DocumentNode_Version_Logical] UNIQUE ([DocumentVersionId], [LogicalNodeId]),
    CONSTRAINT [UQ_DocumentNode_Id_Version_Logical] UNIQUE ([Id], [DocumentVersionId], [LogicalNodeId]),
    CONSTRAINT [FK_DocumentNode_Version] FOREIGN KEY ([DocumentVersionId]) REFERENCES [app].[DocumentVersion] ([Id]),
    -- The parent must belong to the same version.
    CONSTRAINT [FK_DocumentNode_Parent] FOREIGN KEY ([DocumentVersionId], [ParentNodeId]) REFERENCES [app].[DocumentNode] ([DocumentVersionId], [Id]),
    CONSTRAINT [FK_DocumentNode_NodeType] FOREIGN KEY ([NodeTypeId]) REFERENCES [app].[NodeType] ([Id]),
    CONSTRAINT [FK_DocumentNode_CreatedBy] FOREIGN KEY ([CreatedByUserId]) REFERENCES [app].[User] ([Id]),
    CONSTRAINT [FK_DocumentNode_ModifiedBy] FOREIGN KEY ([ModifiedByUserId]) REFERENCES [app].[User] ([Id]),
    CONSTRAINT [CK_DocumentNode_Title_NotEmpty] CHECK (LEN(LTRIM(RTRIM([Title]))) > 0),
    CONSTRAINT [CK_DocumentNode_NotOwnParent] CHECK ([ParentNodeId] IS NULL OR [ParentNodeId] <> [Id])
)
WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = [history].[DocumentNode]), LEDGER = ON (LEDGER_VIEW = [app].[DocumentNode_Ledger] (TRANSACTION_ID_COLUMN_NAME = [ledger_transaction_id], SEQUENCE_NUMBER_COLUMN_NAME = [ledger_sequence_number], OPERATION_TYPE_COLUMN_NAME = [ledger_operation_type], OPERATION_TYPE_DESC_COLUMN_NAME = [ledger_operation_type_desc])));
GO
CREATE NONCLUSTERED INDEX [IX_DocumentNode_Version_Parent_Sort]
    ON [app].[DocumentNode] ([DocumentVersionId], [ParentNodeId], [SortOrder]);
GO
CREATE NONCLUSTERED INDEX [IX_DocumentNode_NodeType]
    ON [app].[DocumentNode] ([NodeTypeId]);
