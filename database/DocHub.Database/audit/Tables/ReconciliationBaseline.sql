-- Reconciliation ignores ledger transactions before the FIRST baseline (T21 §2), resolved across renamed/dropped copies.
CREATE TABLE [audit].[ReconciliationBaseline]
(
    [Id]        INT            IDENTITY (1, 1) NOT NULL,
    [CreatedAt] DATETIME2 (7)  NOT NULL CONSTRAINT [DF_ReconciliationBaseline_CreatedAt] DEFAULT (SYSUTCDATETIME()),
    [Reason]    NVARCHAR (500) NOT NULL,
    CONSTRAINT [PK_ReconciliationBaseline] PRIMARY KEY CLUSTERED ([Id])
)
WITH (LEDGER = ON (APPEND_ONLY = ON, LEDGER_VIEW = [audit].[ReconciliationBaseline_Ledger] (TRANSACTION_ID_COLUMN_NAME = [ledger_transaction_id], SEQUENCE_NUMBER_COLUMN_NAME = [ledger_sequence_number], OPERATION_TYPE_COLUMN_NAME = [ledger_operation_type], OPERATION_TYPE_DESC_COLUMN_NAME = [ledger_operation_type_desc])));
