-- Append-only change log written by the audit triggers (T03, ADR-04). An append-only ledger table (T21): nobody, dbo
-- included, can update or delete rows. Partitioned by month, page-compressed (NFR-L10).
CREATE TABLE [audit].[ChangeLog]
(
    [Id]                BIGINT           IDENTITY (1, 1) NOT NULL,
    [ChangedAt]         DATETIME2 (7)    NOT NULL CONSTRAINT [DF_ChangeLog_ChangedAt] DEFAULT (SYSUTCDATETIME()),
    [TableName]         NVARCHAR (128)   NOT NULL,
    [Operation]         CHAR (1)         NOT NULL,
    [EntityId]          INT              NOT NULL,
    [DocumentId]        INT              NULL,
    [DocumentVersionId] INT              NULL,
    [LogicalNodeId]     UNIQUEIDENTIFIER NULL,
    [OldValues]         NVARCHAR (MAX)   NULL,
    [NewValues]         NVARCHAR (MAX)   NULL,
    [ChangedColumns]    NVARCHAR (1000)  NULL,
    [UserId]            INT              NULL,
    [Source]            VARCHAR (10)     NOT NULL,
    [DbLogin]           NVARCHAR (128)   NOT NULL,
    [AppName]           NVARCHAR (128)   NULL,
    [CorrelationId]     NVARCHAR (64)    NULL,
    [OperationContext]  NVARCHAR (50)    NULL,
    [Ticket]            NVARCHAR (50)    NULL,
    [Reason]            NVARCHAR (500)   NULL,
    -- Ledger columns, declared explicitly so reconciliation can index the transaction id (T21 §4).
    [ledger_start_transaction_id]  BIGINT GENERATED ALWAYS AS TRANSACTION_ID START HIDDEN NOT NULL,
    [ledger_start_sequence_number] BIGINT GENERATED ALWAYS AS SEQUENCE_NUMBER START HIDDEN NOT NULL,
    CONSTRAINT [PK_ChangeLog] PRIMARY KEY CLUSTERED ([Id], [ChangedAt]) WITH (DATA_COMPRESSION = PAGE) ON [ps_ChangeLog_Month] ([ChangedAt]),
    CONSTRAINT [CK_ChangeLog_Operation] CHECK ([Operation] = 'I' OR [Operation] = 'U' OR [Operation] = 'D'),
    CONSTRAINT [CK_ChangeLog_Source] CHECK ([Source] = 'App' OR [Source] = 'Script'),
    CONSTRAINT [CK_ChangeLog_OldValues] CHECK ([OldValues] IS NULL OR ISJSON([OldValues]) = 1),
    CONSTRAINT [CK_ChangeLog_NewValues] CHECK ([NewValues] IS NULL OR ISJSON([NewValues]) = 1)
) ON [ps_ChangeLog_Month] ([ChangedAt])
WITH (LEDGER = ON (APPEND_ONLY = ON, LEDGER_VIEW = [audit].[ChangeLog_Ledger] (TRANSACTION_ID_COLUMN_NAME = [ledger_transaction_id], SEQUENCE_NUMBER_COLUMN_NAME = [ledger_sequence_number], OPERATION_TYPE_COLUMN_NAME = [ledger_operation_type], OPERATION_TYPE_DESC_COLUMN_NAME = [ledger_operation_type_desc])));
GO
CREATE NONCLUSTERED INDEX [IX_ChangeLog_LogicalNode] ON [audit].[ChangeLog] ([LogicalNodeId], [ChangedAt])
    WHERE [LogicalNodeId] IS NOT NULL WITH (DATA_COMPRESSION = PAGE) ON [ps_ChangeLog_Month] ([ChangedAt]);
GO
CREATE NONCLUSTERED INDEX [IX_ChangeLog_Document] ON [audit].[ChangeLog] ([DocumentId], [ChangedAt])
    WHERE [DocumentId] IS NOT NULL WITH (DATA_COMPRESSION = PAGE) ON [ps_ChangeLog_Month] ([ChangedAt]);
GO
CREATE NONCLUSTERED INDEX [IX_ChangeLog_Entity] ON [audit].[ChangeLog] ([TableName], [EntityId], [ChangedAt])
    WITH (DATA_COMPRESSION = PAGE) ON [ps_ChangeLog_Month] ([ChangedAt]);
GO
CREATE NONCLUSTERED INDEX [IX_ChangeLog_Table] ON [audit].[ChangeLog] ([TableName], [ChangedAt])
    WITH (DATA_COMPRESSION = PAGE) ON [ps_ChangeLog_Month] ([ChangedAt]);
GO
CREATE NONCLUSTERED INDEX [IX_ChangeLog_User] ON [audit].[ChangeLog] ([UserId], [ChangedAt])
    WHERE [UserId] IS NOT NULL WITH (DATA_COMPRESSION = PAGE) ON [ps_ChangeLog_Month] ([ChangedAt]);
GO
CREATE NONCLUSTERED INDEX [IX_ChangeLog_DbLogin] ON [audit].[ChangeLog] ([DbLogin], [ChangedAt])
    WITH (DATA_COMPRESSION = PAGE) ON [ps_ChangeLog_Month] ([ChangedAt]);
GO
CREATE NONCLUSTERED INDEX [IX_ChangeLog_Ticket] ON [audit].[ChangeLog] ([Ticket], [ChangedAt])
    WHERE [Ticket] IS NOT NULL WITH (DATA_COMPRESSION = PAGE) ON [ps_ChangeLog_Month] ([ChangedAt]);
GO
CREATE NONCLUSTERED INDEX [IX_ChangeLog_LedgerTransaction] ON [audit].[ChangeLog] ([ledger_start_transaction_id])
    INCLUDE ([TableName], [EntityId]) WITH (DATA_COMPRESSION = PAGE) ON [ps_ChangeLog_Month] ([ChangedAt]);
