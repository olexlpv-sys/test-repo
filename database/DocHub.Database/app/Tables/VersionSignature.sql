-- Temporal + updatable ledger table (T21): immutable history, FOR SYSTEM_TIME queries, principal of every transaction.
CREATE TABLE [app].[VersionSignature]
(
    [Id]                INT             IDENTITY (1, 1) NOT NULL,
    [DocumentVersionId] INT             NOT NULL,
    [UserId]            INT             NOT NULL,
    [SignedAt]          DATETIME2 (3)   NOT NULL CONSTRAINT [DF_VersionSignature_SignedAt] DEFAULT (SYSUTCDATETIME()),
    -- Hash of the draft content when signing; the signature is valid only while it equals the current hash (FR-V6).
    [ContentHash]       VARBINARY (32)  NOT NULL,
    [WithdrawnAt]       DATETIME2 (3)   NULL,
    [Comment]           NVARCHAR (1000) NULL,
    [ValidFrom]         DATETIME2 (7)    GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
    [ValidTo]           DATETIME2 (7)    GENERATED ALWAYS AS ROW END HIDDEN NOT NULL,
    PERIOD FOR SYSTEM_TIME ([ValidFrom], [ValidTo]),
    CONSTRAINT [PK_VersionSignature] PRIMARY KEY CLUSTERED ([Id]),
    CONSTRAINT [FK_VersionSignature_Version] FOREIGN KEY ([DocumentVersionId]) REFERENCES [app].[DocumentVersion] ([Id]),
    CONSTRAINT [FK_VersionSignature_User] FOREIGN KEY ([UserId]) REFERENCES [app].[User] ([Id]),
    CONSTRAINT [CK_VersionSignature_ContentHash] CHECK (DATALENGTH([ContentHash]) = 32),
    CONSTRAINT [CK_VersionSignature_Withdrawn] CHECK ([WithdrawnAt] IS NULL OR [WithdrawnAt] >= [SignedAt])
)
WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = [history].[VersionSignature]), LEDGER = ON (LEDGER_VIEW = [app].[VersionSignature_Ledger] (TRANSACTION_ID_COLUMN_NAME = [ledger_transaction_id], SEQUENCE_NUMBER_COLUMN_NAME = [ledger_sequence_number], OPERATION_TYPE_COLUMN_NAME = [ledger_operation_type], OPERATION_TYPE_DESC_COLUMN_NAME = [ledger_operation_type_desc])));
GO
-- One active signature per approver and version.
CREATE UNIQUE NONCLUSTERED INDEX [UX_VersionSignature_Active]
    ON [app].[VersionSignature] ([DocumentVersionId], [UserId]) WHERE [WithdrawnAt] IS NULL;
