-- Findings of audit.usp_ReconcileLedger and the API's module-integrity check (T21 §4). Append-only ledger table.
CREATE TABLE [audit].[ReconciliationFinding]
(
    [Id]                    BIGINT           IDENTITY (1, 1) NOT NULL,
    [Kind]                  VARCHAR (40)     NOT NULL,
    [TableName]             NVARCHAR (256)   NULL,
    [EntityId]              INT              NULL,
    [DocumentId]            INT              NULL,
    [DocumentVersionId]     INT              NULL,
    [LedgerTransactionId]   BIGINT           NULL,
    [TransactionCommitTime] DATETIME2 (7)    NULL,
    [Principal]             NVARCHAR (256)   NULL,
    [Detail]                NVARCHAR (1000)  NULL,
    [DetectedAt]            DATETIME2 (7)    NOT NULL CONSTRAINT [DF_ReconciliationFinding_DetectedAt] DEFAULT (SYSUTCDATETIME()),
    -- The transaction committed at or after the version's ledger signing transaction (FR-H5 "modified after signing").
    [AfterSigning]          BIT              NOT NULL CONSTRAINT [DF_ReconciliationFinding_AfterSigning] DEFAULT (0),
    CONSTRAINT [PK_ReconciliationFinding] PRIMARY KEY CLUSTERED ([Id]),
    CONSTRAINT [CK_ReconciliationFinding_Kind] CHECK ([Kind] = 'TriggerBypass' OR [Kind] = 'ForgedAuditRow' OR [Kind] = 'StampTampering'
        OR [Kind] = 'LedgerIntegrity' OR [Kind] = 'SchemaTampering' OR [Kind] = 'ModuleIntegrity')
)
WITH (LEDGER = ON (APPEND_ONLY = ON, LEDGER_VIEW = [audit].[ReconciliationFinding_Ledger] (TRANSACTION_ID_COLUMN_NAME = [ledger_transaction_id], SEQUENCE_NUMBER_COLUMN_NAME = [ledger_sequence_number], OPERATION_TYPE_COLUMN_NAME = [ledger_operation_type], OPERATION_TYPE_DESC_COLUMN_NAME = [ledger_operation_type_desc])));
GO
-- A finding is unique by kind + object + entity + ledger transaction, so re-runs add nothing. Findings without a ledger
-- transaction (ledger integrity, module integrity) are unique by kind + object + detail, checked by the writers.
CREATE UNIQUE NONCLUSTERED INDEX [UX_ReconciliationFinding_Identity]
    ON [audit].[ReconciliationFinding] ([Kind], [TableName], [EntityId], [LedgerTransactionId]) WHERE [LedgerTransactionId] IS NOT NULL;
GO
CREATE NONCLUSTERED INDEX [IX_ReconciliationFinding_Version]
    ON [audit].[ReconciliationFinding] ([DocumentVersionId]) INCLUDE ([AfterSigning], [TransactionCommitTime]) WHERE [DocumentVersionId] IS NOT NULL;
