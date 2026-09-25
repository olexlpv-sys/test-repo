-- Temporal + updatable ledger table (T21): immutable history, FOR SYSTEM_TIME queries, principal of every transaction.
CREATE TABLE [app].[ContentStyle]
(
    [Id]             INT            IDENTITY (1, 1) NOT NULL,
    [StyleId]        VARCHAR (50)   NOT NULL,
    [Name]           NVARCHAR (100) NOT NULL,
    [Kind]           TINYINT        NOT NULL,
    [BasedOnStyleId] VARCHAR (50)   NULL,
    [PropertiesJson] NVARCHAR (MAX) NOT NULL,
    [IsBuiltIn]      BIT            NOT NULL CONSTRAINT [DF_ContentStyle_IsBuiltIn] DEFAULT (0),
    [IsActive]       BIT            NOT NULL CONSTRAINT [DF_ContentStyle_IsActive] DEFAULT (1),
    [RowVersion]     ROWVERSION     NOT NULL,
    [ValidFrom]         DATETIME2 (7)    GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
    [ValidTo]           DATETIME2 (7)    GENERATED ALWAYS AS ROW END HIDDEN NOT NULL,
    PERIOD FOR SYSTEM_TIME ([ValidFrom], [ValidTo]),
    CONSTRAINT [PK_ContentStyle] PRIMARY KEY CLUSTERED ([Id]),
    CONSTRAINT [UQ_ContentStyle_StyleId] UNIQUE ([StyleId]),
    CONSTRAINT [FK_ContentStyle_BasedOn] FOREIGN KEY ([BasedOnStyleId]) REFERENCES [app].[ContentStyle] ([StyleId]),
    -- 1 Paragraph, 2 Character, 3 Table
    CONSTRAINT [CK_ContentStyle_Kind] CHECK ([Kind] >= 1 AND [Kind] <= 3),
    CONSTRAINT [CK_ContentStyle_PropertiesJson] CHECK (ISJSON([PropertiesJson]) = 1),
    CONSTRAINT [CK_ContentStyle_NotBasedOnSelf] CHECK ([BasedOnStyleId] IS NULL OR [BasedOnStyleId] <> [StyleId])
)
WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = [history].[ContentStyle]), LEDGER = ON (LEDGER_VIEW = [app].[ContentStyle_Ledger] (TRANSACTION_ID_COLUMN_NAME = [ledger_transaction_id], SEQUENCE_NUMBER_COLUMN_NAME = [ledger_sequence_number], OPERATION_TYPE_COLUMN_NAME = [ledger_operation_type], OPERATION_TYPE_DESC_COLUMN_NAME = [ledger_operation_type_desc])));
