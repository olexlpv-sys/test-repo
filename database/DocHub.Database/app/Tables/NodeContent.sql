-- Temporal + updatable ledger table (T21): immutable history, FOR SYSTEM_TIME queries, principal of every transaction.
CREATE TABLE [app].[NodeContent]
(
    [NodeId]            INT              NOT NULL,
    -- Redundant copies of the node's version and logical id: keep audit rows resolvable after cascade deletes (T03).
    [DocumentVersionId] INT              NOT NULL,
    [LogicalNodeId]     UNIQUEIDENTIFIER NOT NULL,
    [SchemaVersion]     TINYINT          NOT NULL CONSTRAINT [DF_NodeContent_SchemaVersion] DEFAULT (1),
    -- Source of truth: TipTap/ProseMirror JSON (docs/content-format.md). The other content columns are derived.
    [ContentJson]       NVARCHAR (MAX)   NOT NULL CONSTRAINT [DF_NodeContent_ContentJson] DEFAULT (N'{"type":"doc","content":[]}'),
    [ContentHtml]       NVARCHAR (MAX)   NOT NULL CONSTRAINT [DF_NodeContent_ContentHtml] DEFAULT (N''),
    [PlainText]         NVARCHAR (MAX)   NOT NULL CONSTRAINT [DF_NodeContent_PlainText] DEFAULT (N''),
    [ContentHash]       VARBINARY (32)   NOT NULL,
    [DerivedStale]      BIT              NOT NULL CONSTRAINT [DF_NodeContent_DerivedStale] DEFAULT (0),
    [ModifiedAt]        DATETIME2 (3)    NOT NULL CONSTRAINT [DF_NodeContent_ModifiedAt] DEFAULT (SYSUTCDATETIME()),
    [ModifiedByUserId]  INT              NOT NULL,
    [RowVersion]        ROWVERSION       NOT NULL,
    [ValidFrom]         DATETIME2 (7)    GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
    [ValidTo]           DATETIME2 (7)    GENERATED ALWAYS AS ROW END HIDDEN NOT NULL,
    PERIOD FOR SYSTEM_TIME ([ValidFrom], [ValidTo]),
    CONSTRAINT [PK_NodeContent] PRIMARY KEY CLUSTERED ([NodeId]),
    CONSTRAINT [FK_NodeContent_Node] FOREIGN KEY ([NodeId], [DocumentVersionId], [LogicalNodeId])
        REFERENCES [app].[DocumentNode] ([Id], [DocumentVersionId], [LogicalNodeId]) ON DELETE CASCADE,
    CONSTRAINT [FK_NodeContent_ModifiedBy] FOREIGN KEY ([ModifiedByUserId]) REFERENCES [app].[User] ([Id]),
    CONSTRAINT [CK_NodeContent_ContentJson] CHECK (ISJSON([ContentJson]) = 1),
    CONSTRAINT [CK_NodeContent_SchemaVersion] CHECK ([SchemaVersion] >= 1),
    CONSTRAINT [CK_NodeContent_ContentHash] CHECK (DATALENGTH([ContentHash]) = 32)
)
WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = [history].[NodeContent]), LEDGER = ON (LEDGER_VIEW = [app].[NodeContent_Ledger] (TRANSACTION_ID_COLUMN_NAME = [ledger_transaction_id], SEQUENCE_NUMBER_COLUMN_NAME = [ledger_sequence_number], OPERATION_TYPE_COLUMN_NAME = [ledger_operation_type], OPERATION_TYPE_DESC_COLUMN_NAME = [ledger_operation_type_desc])));
GO
CREATE NONCLUSTERED INDEX [IX_NodeContent_DerivedStale]
    ON [app].[NodeContent] ([NodeId]) WHERE [DerivedStale] = 1;
GO
CREATE NONCLUSTERED INDEX [IX_NodeContent_Version]
    ON [app].[NodeContent] ([DocumentVersionId]);
