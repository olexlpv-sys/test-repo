-- Temporal + updatable ledger table (T21): immutable history, FOR SYSTEM_TIME queries, principal of every transaction.
CREATE TABLE [app].[NodeType]
(
    [Id]          INT            IDENTITY (1, 1) NOT NULL,
    [Code]        VARCHAR (50)   NOT NULL,
    [Name]        NVARCHAR (100) NOT NULL,
    [Description] NVARCHAR (500) NULL,
    [SortOrder]   INT            NOT NULL CONSTRAINT [DF_NodeType_SortOrder] DEFAULT (0),
    [IsActive]    BIT            NOT NULL CONSTRAINT [DF_NodeType_IsActive] DEFAULT (1),
    [RowVersion]  ROWVERSION     NOT NULL,
    [ValidFrom]         DATETIME2 (7)    GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
    [ValidTo]           DATETIME2 (7)    GENERATED ALWAYS AS ROW END HIDDEN NOT NULL,
    PERIOD FOR SYSTEM_TIME ([ValidFrom], [ValidTo]),
    CONSTRAINT [PK_NodeType] PRIMARY KEY CLUSTERED ([Id]),
    CONSTRAINT [UQ_NodeType_Code] UNIQUE ([Code]),
    CONSTRAINT [CK_NodeType_Name_NotEmpty] CHECK (LEN([Name]) > 0)
)
WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = [history].[NodeType]), LEDGER = ON (LEDGER_VIEW = [app].[NodeType_Ledger] (TRANSACTION_ID_COLUMN_NAME = [ledger_transaction_id], SEQUENCE_NUMBER_COLUMN_NAME = [ledger_sequence_number], OPERATION_TYPE_COLUMN_NAME = [ledger_operation_type], OPERATION_TYPE_DESC_COLUMN_NAME = [ledger_operation_type_desc])));
