-- PDF export jobs (T20, FR-E5/E7): one row per request and requester — also for cache hits, so every export is audited.
-- Temporal + updatable ledger table (T21) like every audited table. Status: 0 Queued, 1 Running, 2 Succeeded, 3 Failed.
CREATE TABLE [app].[ExportJob]
(
    [Id]                INT              IDENTITY (1, 1) NOT NULL,
    [DocumentId]        INT              NOT NULL,
    [DocumentVersionId] INT              NOT NULL,
    -- NULL = the whole document; else the exported subtree's root.
    [LogicalNodeId]     UNIQUEIDENTIFIER NULL,
    [OptionsJson]       NVARCHAR (1000)  NOT NULL,
    [Status]            TINYINT          NOT NULL CONSTRAINT [DF_ExportJob_Status] DEFAULT (0),
    [Progress]          TINYINT          NOT NULL CONSTRAINT [DF_ExportJob_Progress] DEFAULT (0),
    [RequestedByUserId] INT              NOT NULL,
    [RequestedAt]       DATETIME2 (3)    NOT NULL CONSTRAINT [DF_ExportJob_RequestedAt] DEFAULT (SYSUTCDATETIME()),
    [StartedAt]         DATETIME2 (3)    NULL,
    [FinishedAt]        DATETIME2 (3)    NULL,
    [Error]             NVARCHAR (2000)  NULL,
    [BlobPath]          NVARCHAR (400)   NULL,
    [FileName]          NVARCHAR (300)   NULL,
    [FileSize]          BIGINT           NULL,
    -- SHA-256 (hex) of version stamp, document row version, folder, style catalog version and options (T20 "Storage").
    [CacheKey]          CHAR (64)        NOT NULL,
    [RowVersion]        ROWVERSION       NOT NULL,
    [ValidFrom]         DATETIME2 (7)    GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
    [ValidTo]           DATETIME2 (7)    GENERATED ALWAYS AS ROW END HIDDEN NOT NULL,
    PERIOD FOR SYSTEM_TIME ([ValidFrom], [ValidTo]),
    CONSTRAINT [PK_ExportJob] PRIMARY KEY CLUSTERED ([Id]),
    CONSTRAINT [FK_ExportJob_Document] FOREIGN KEY ([DocumentId]) REFERENCES [app].[Document] ([Id]),
    CONSTRAINT [FK_ExportJob_Version] FOREIGN KEY ([DocumentVersionId]) REFERENCES [app].[DocumentVersion] ([Id]),
    CONSTRAINT [FK_ExportJob_RequestedBy] FOREIGN KEY ([RequestedByUserId]) REFERENCES [app].[User] ([Id]),
    CONSTRAINT [CK_ExportJob_Status] CHECK ([Status] <= 3),
    CONSTRAINT [CK_ExportJob_Progress] CHECK ([Progress] <= 100),
    CONSTRAINT [CK_ExportJob_Succeeded] CHECK ([Status] <> 2 OR ([BlobPath] IS NOT NULL AND [FinishedAt] IS NOT NULL)),
    CONSTRAINT [CK_ExportJob_CacheKey] CHECK ([CacheKey] NOT LIKE '%[^0-9a-f]%')
)
WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = [history].[ExportJob]), LEDGER = ON (LEDGER_VIEW = [app].[ExportJob_Ledger] (TRANSACTION_ID_COLUMN_NAME = [ledger_transaction_id], SEQUENCE_NUMBER_COLUMN_NAME = [ledger_sequence_number], OPERATION_TYPE_COLUMN_NAME = [ledger_operation_type], OPERATION_TYPE_DESC_COLUMN_NAME = [ledger_operation_type_desc])));
GO
-- The worker claims the oldest queued job (READPAST, UPDLOCK).
CREATE NONCLUSTERED INDEX [IX_ExportJob_Queue]
    ON [app].[ExportJob] ([Status], [RequestedAt]) WHERE [Status] IN (0, 1);
GO
-- Cache hits and a requester's pending job for the same cache key.
CREATE NONCLUSTERED INDEX [IX_ExportJob_CacheKey]
    ON [app].[ExportJob] ([CacheKey], [Status]) INCLUDE ([RequestedByUserId], [BlobPath], [FinishedAt]);
