-- Per-version change stamp, maintained by the audit triggers (T03); not audited itself, but an updatable ledger table (T21)
-- so a cleared TamperedAt stays visible and reconciliation can check who changed it.
CREATE TABLE [app].[VersionStamp]
(
    [DocumentVersionId] INT           NOT NULL,
    -- audit.ChangeLog.Id of the latest change to the version (cache key and ETag, NFR-L9).
    [LastChangeLogId]   BIGINT        NOT NULL,
    -- First change to nodes/content of a Signed version after signing (FR-H5).
    [TamperedAt]        DATETIME2 (7) NULL,
    CONSTRAINT [PK_VersionStamp] PRIMARY KEY CLUSTERED ([DocumentVersionId]),
    CONSTRAINT [FK_VersionStamp_Version] FOREIGN KEY ([DocumentVersionId]) REFERENCES [app].[DocumentVersion] ([Id]) ON DELETE CASCADE
)
WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = [history].[VersionStamp]), LEDGER = ON (LEDGER_VIEW = [app].[VersionStamp_Ledger] (TRANSACTION_ID_COLUMN_NAME = [ledger_transaction_id], SEQUENCE_NUMBER_COLUMN_NAME = [ledger_sequence_number], OPERATION_TYPE_COLUMN_NAME = [ledger_operation_type], OPERATION_TYPE_DESC_COLUMN_NAME = [ledger_operation_type_desc])));
